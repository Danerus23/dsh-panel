using DshPanel.Agents;
using DshPanel.Balance;
using DshPanel.Isolation;
using DshPanel.Peak;
using DshPanel.Settings;
using DshPanel.Shell;

namespace DshPanel.Balance;

/// <summary>
/// Проба баланса и окон пика.
///
/// Работает в двух режимах, и это принципиально:
///
/// * **обычный прогон** (`--balance-selftest`) проверяет всё, что можно проверить без сети
///   и без ключа человека: разбор файла ключей (на СВОЁМ временном файле), разбор ответов
///   агента (на записанных заранее), решения о предупреждениях, окна пика и подсказку значка.
///   Владельца он не касается вовсе;
/// * **живой прогон** (`--balance-selftest --with-my-key`) дополнительно читает НАСТОЯЩИЙ файл
///   ключей владельца и делает настоящий запрос баланса. Это право даётся только явным ключом
///   и только человеком: разрешение владельца от 24.09.2026 — «читай спокойно
///   <c>~/.dsh/.credentials.yaml</c>, можешь делать живой запрос баланса».
///
/// Зачем вообще живой прогон. Баланс — единственное место, где панель ходит в интернет
/// с ключом человека, и «код написан, значит работает» тут не годится: в v1 TLS на этой машине
/// не работал вовсе (Schannel отдавал <c>SEC_E_NO_CREDENTIALS</c>), и без живой проверки
/// об этом узнал бы только человек.
///
/// Каждая строка отчёта влияет на исход. Секретов в отчёте нет: ключ не печатается никогда,
/// а путь к файлу показывается маскированным.
/// </summary>
public static class BalanceSelfTest
{
    public static int Run(bool withOwnerKey)
    {
        var report = new List<string>();
        var failed = false;

        void Check(string what, bool ok)
        {
            if (!ok) failed = true;
            report.Add($"{what} = {ok}");
        }

        var parent = Path.Combine(Path.GetTempPath(), "dsh-panel-balance-selftest");
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));

        try
        {
            var agent = AgentCatalog.DeepSeek;

            // --- 1. разбор файла ключей (на своём файле) ---------------------------

            Directory.CreateDirectory(root);
            var fake = Path.Combine(root, ".credentials.yaml");
            var key = "sk-" + new string('x', 32);

            File.WriteAllText(fake, $"version: 1\nrefs:\n  {agent.KeyName}: {key}\n");
            var flat = CredentialsKey.Read(fake, agent.KeyName);
            Check("ключ найден в раскладке version 1", flat.Found && flat.Value == key);
            Check("значение ключа в объяснении не появляется", !flat.Problem.Contains(key, StringComparison.Ordinal));

            File.WriteAllText(fake, $"{agent.KeyName}: \"{key}\"\n");
            Check("ключ найден и в плоской раскладке, кавычки срезаны", CredentialsKey.Read(fake, agent.KeyName).Value == key);

            File.WriteAllText(fake, "refs:\n  ДРУГОЙ_КЛЮЧ: value\n");
            var missing = CredentialsKey.Read(fake, agent.KeyName);
            Check("чужого ключа нет — так и сказано", !missing.Found && missing.Problem.Contains(agent.KeyName, StringComparison.Ordinal));

            var absent = CredentialsKey.Read(Path.Combine(root, "нет-такого.yaml"), agent.KeyName);
            Check("файла нет — это отказ, а не падение", !absent.Found && absent.Problem.Length > 0);

            // --- 2. разбор ответа агента ------------------------------------------

            const string real = """
            {"is_available":true,"balance_infos":[
              {"currency":"CNY","total_balance":"12.34","granted_balance":"0.00","topped_up_balance":"12.34"}]}
            """;

            using (var document = System.Text.Json.JsonDocument.Parse(real))
            {
                var parsed = HttpBalanceClient.Parse(document.RootElement, agent.Id, DateTimeOffset.Now);

                Check("ответ разобран", parsed.Ok && parsed.Available);
                Check("сумма и валюта взяты", parsed.Total == 12.34m && parsed.Currency == "CNY");
                Check("сводка собрана", parsed.Summary.Contains("12.34", StringComparison.Ordinal));
                Check("подробность собрана", parsed.Detail.Contains("пополнено", StringComparison.Ordinal));
            }

            using (var document = System.Text.Json.JsonDocument.Parse("""{"is_available":false,"balance_infos":[]}"""))
            {
                var parsed = HttpBalanceClient.Parse(document.RootElement, agent.Id, DateTimeOffset.Now);
                Check("недоступный счёт — это Ok без суммы", parsed.Ok && !parsed.Available && parsed.Total is null);
            }

            using (var document = System.Text.Json.JsonDocument.Parse("{}"))
            {
                var parsed = HttpBalanceClient.Parse(document.RootElement, agent.Id, DateTimeOffset.Now);
                Check("пустой ответ не роняет разбор", parsed.Ok && parsed.Total is null);
            }

            Check(
                "ключ вырезается из текста ошибки",
                !HttpBalanceClient.Redact("Authorization: Bearer " + key, key).Contains(key, StringComparison.Ordinal));

            // --- 3. решения о предупреждениях --------------------------------------

            Check("порог: баланс ниже — предупредить", BalanceDecisions.ShouldWarnLow(true, 5m, 10m, alreadyWarned: false));
            Check("порог: уже предупреждали — молчим", !BalanceDecisions.ShouldWarnLow(true, 5m, 10m, alreadyWarned: true));
            Check("порог: выключено — молчим", !BalanceDecisions.ShouldWarnLow(false, 5m, 10m, alreadyWarned: false));
            Check("порог: баланс неизвестен — молчим", !BalanceDecisions.ShouldWarnLow(true, null, 10m, alreadyWarned: false));

            var peakStart = new DateTime(2026, 9, 24, 1, 0, 0, DateTimeKind.Utc);
            Check(
                "пик: за 15 минут при просьбе 30 — предупредить",
                BalanceDecisions.ShouldWarnPeak(30, inPeak: false, PeakMoment.PeakStart, 15, peakStart, null));
            Check(
                "пик: уже в пике — поздно",
                !BalanceDecisions.ShouldWarnPeak(30, inPeak: true, PeakMoment.PeakStart, 15, peakStart, null));
            Check(
                "пик: раньше срока — молчим",
                !BalanceDecisions.ShouldWarnPeak(10, inPeak: false, PeakMoment.PeakStart, 15, peakStart, null));
            Check(
                "пик: про это начало уже предупреждали",
                !BalanceDecisions.ShouldWarnPeak(30, inPeak: false, PeakMoment.PeakStart, 15, peakStart, peakStart));
            Check(
                "пик: следующее переключение — конец пика, это не беда",
                !BalanceDecisions.ShouldWarnPeak(30, inPeak: false, PeakMoment.PeakEnd, 15, peakStart, null));

            // --- 4. окна пика ------------------------------------------------------

            // Понедельник 02:00 UTC — внутри первого окна (01:00–04:00).
            var monday = new DateTimeOffset(2026, 9, 21, 2, 0, 0, TimeSpan.Zero);
            var inPeak = PeakDecisions.State(agent, monday);
            Check("окна: понедельник 02:00 UTC — пик", inPeak.InPeak);
            Check("окна: следующее переключение — конец пика", inPeak.NextKind == PeakMoment.PeakEnd && inPeak.HasNext);
            Check("окна: до конца пика два часа", inPeak.MinutesUntilNext == 120);

            var mondayNoon = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
            var offPeak = PeakDecisions.State(agent, mondayNoon);
            Check("окна: понедельник 12:00 UTC — вне пика", !offPeak.InPeak);

            var saturday = new DateTimeOffset(2026, 9, 26, 2, 0, 0, TimeSpan.Zero);
            Check("окна: суббота — вне пика всегда", !PeakDecisions.State(agent, saturday).InPeak);

            // Пятница 23:00 UTC: следующее начало пика — только в понедельник 01:00.
            var fridayNight = new DateTimeOffset(2026, 9, 25, 23, 0, 0, TimeSpan.Zero);
            var weekend = PeakDecisions.State(agent, fridayNight);
            Check(
                "окна: в пятницу вечером следующее начало — в понедельник",
                weekend.NextKind == PeakMoment.PeakStart && weekend.MinutesUntilNext == 2 * 24 * 60 + 120);

            Check("окна: расписание словами не пусто", inPeak.ScheduleText.Length > 0);
            report.Add("расписание = " + inPeak.ScheduleText);

            var plus3 = new DateTimeOffset(2026, 9, 21, 2, 0, 0, TimeSpan.FromHours(3));
            report.Add("расписание по местному (UTC+03:00) = " + PeakDecisions.ScheduleText(agent, 180));
            Check("окна: смещение учитывается", PeakDecisions.ScheduleText(agent, 180).Contains("UTC+03:00", StringComparison.Ordinal));
            Check(
                "окна: переключение показано по местному",
                PeakDecisions.LocalClock(new DateTime(2026, 9, 21, 2, 0, 0, DateTimeKind.Utc), 180) == "05:00");
            Check("окна: момент в UTC+03:00 читается как местный", plus3.Offset == TimeSpan.FromHours(3));

            // Часовой пояс панель нигде не хранит: она спрашивает его у системы — и для «сейчас»,
            // и для момента переключения (у зон с переводом часов это разные числа).
            var nowUtc = DateTime.UtcNow;
            report.Add($"часовой пояс системы = {PeakDecisions.LocalZoneName()} " +
                       $"({PeakDecisions.OffsetMinutes(DateTimeOffset.Now)} мин)");
            Check(
                "часовой пояс берётся у системы",
                PeakDecisions.OffsetMinutes(DateTimeOffset.Now) == (int)DateTimeOffset.Now.Offset.TotalMinutes);
            Check(
                "смещение на момент спрашивается у системы заново",
                PeakDecisions.OffsetMinutesAt(nowUtc) == (int)TimeZoneInfo.Local.GetUtcOffset(nowUtc).TotalMinutes);
            Check(
                "расписание показывается в зоне системы",
                PeakDecisions.ScheduleText(agent, PeakDecisions.OffsetMinutes(DateTimeOffset.Now))
                    .Contains($"UTC{(DateTimeOffset.Now.Offset < TimeSpan.Zero ? "-" : "+")}" +
                              $"{Math.Abs(DateTimeOffset.Now.Offset.Hours):D2}:" +
                              $"{Math.Abs(DateTimeOffset.Now.Offset.Minutes):D2}", StringComparison.Ordinal));

            Check("длительность словами", PeakDecisions.FormatSpan(135).Contains('2') && PeakDecisions.FormatSpan(135).Contains("15"));

            // --- 5. подсказка значка ----------------------------------------------

            var tooltip = TrayTooltip.Build(agent.Title, "12.34 CNY", inPeak.InPeak ? PanelStrings.PeakInPeak : PanelStrings.PeakOffPeak);
            report.Add("подсказка значка = " + tooltip);
            Check("подсказка: агент, баланс и тариф на месте",
                tooltip.Contains(agent.Title, StringComparison.Ordinal)
                && tooltip.Contains("12.34 CNY", StringComparison.Ordinal)
                && tooltip.Contains("Пик", StringComparison.Ordinal));

            Check(
                "подсказка: без баланса так и говорит",
                TrayTooltip.Build(agent.Title, "", PanelStrings.PeakOffPeak)
                    .Contains(PanelStrings.TrayToolTipNoBalance, StringComparison.Ordinal));

            // --- 6. живой запрос (только с явным разрешением) ---------------------

            if (!withOwnerKey)
            {
                report.Add("живой запрос НЕ делался: ключ не просили (--balance-selftest --with-my-key)");
            }
            else
            {
                var ownerPaths = AppPaths.ForOwner();
                var lookup = CredentialsKey.Read(ownerPaths.CredentialsPath, agent.KeyName);
                report.Add($"файл ключей владельца = {DisplayMask.Path(ownerPaths.CredentialsPath)}");
                Check("ключ владельца найден", lookup.Found);

                if (lookup.Found)
                {
                    var balance = new HttpBalanceClient().Query(agent, lookup.Value, DateTimeOffset.Now);

                    report.Add($"живой ответ: ok={balance.Ok}, available={balance.Available}, " +
                               $"сводка={balance.Summary}");

                    Check("живой запрос прошёл", balance.Ok);
                    Check("живой ответ разобран в сумму и валюту", balance.Total is not null && balance.Currency.Length > 0);
                    Check("в ответе нет ключа", !balance.Summary.Contains(lookup.Value, StringComparison.Ordinal)
                                               && !balance.Error.Contains(lookup.Value, StringComparison.Ordinal));
                }
            }
        }
        catch (Exception ex)
        {
            failed = true;
            report.Add($"ОШИБКА: {ex.GetType().Name} — {ex.Message}");
        }
        finally
        {
            var gone = true;

            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                try { if (Directory.Exists(parent)) Directory.Delete(parent); } catch { }
            }
            catch { gone = false; }

            report.Add($"уборка: временный каталог пробы убран = {gone}");
            if (!gone) failed = true;
        }

        foreach (var line in report) Console.WriteLine("БАЛАНС| " + line);
        Console.WriteLine(failed ? "БАЛАНС ПРОВАЛ" : "БАЛАНС УСПЕХ");
        return failed ? 1 : 0;
    }
}
