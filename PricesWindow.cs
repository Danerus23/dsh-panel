using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// Большое окно со ценами за 1M токенов: таблица со столбцами по моделям,
/// растягивается как обычное окно. Данные — только справка со страницы цен,
/// никуда не применяются.
/// </summary>
public sealed class PricesWindow : Form
{
    private readonly Label _header = new();
    private readonly ListView _list = new();
    private readonly Button _copy = new();
    private readonly Label _empty = new();

    public PricesWindow(PricingCheckResult result, string sourceUrl)
    {
        Text = Loc.T("prices.title");
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 440);
        MinimumSize = new Size(560, 320);
        // Раньше окно было на 10pt — крупнее всех остальных окон продукта. Рабочий текст
        // у всего продукта один, роль Body: разнобой из описи уходит.
        Font = Theme.Body;
        BackColor = Theme.Colors.Window;
        AutoScaleMode = AutoScaleMode.Font;

        BuildLayout();
        Theme.Apply(this);
        Fill(result, sourceUrl);
    }

    private void BuildLayout()
    {
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 48 };

        _copy.Text = Loc.T("prices.copy");
        _copy.Location = new Point(534, 8);
        _copy.Size = new Size(120, 32);
        _copy.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _copy.Click += (_, _) => CopyToClipboard();
        buttons.Controls.Add(_copy);

        var close = new Button
        {
            Text = Loc.T("prices.close"),
            DialogResult = DialogResult.OK,
            Location = new Point(662, 8),
            Size = new Size(90, 32),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        buttons.Controls.Add(close);
        buttons.Resize += (_, _) =>
        {
            close.Left = buttons.ClientSize.Width - close.Width - 8;
            _copy.Left = close.Left - _copy.Width - 8;
        };

        _header.Dock = DockStyle.Top;
        _header.Height = 48;
        _header.Padding = new Padding(12, 8, 12, 0);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.HideSelection = false;
        _list.MultiSelect = true;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;

        _empty.Dock = DockStyle.Fill;
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.ForeColor = Theme.Colors.Faint;
        _empty.Visible = false;

        Controls.Add(_list);
        Controls.Add(_empty);
        Controls.Add(_header);
        Controls.Add(buttons);

        AcceptButton = close;
        CancelButton = close;
    }

    private void Fill(PricingCheckResult result, string sourceUrl)
    {
        var source = string.IsNullOrWhiteSpace(sourceUrl)
            ? (result != null && !string.IsNullOrWhiteSpace(result.SourceUrl) ? result.SourceUrl : Loc.T("prices.source.official"))
            : sourceUrl;

        if (result == null || !result.PricesParsed || result.Prices.Count == 0)
        {
            _header.Text = Loc.T("prices.header.plain");
            _empty.Text = result == null
                ? Loc.T("prices.empty.noData")
                : Loc.T("prices.empty.unparsed");
            _empty.Visible = true;
            _list.Visible = false;
            _copy.Enabled = false;
            return;
        }

        _header.Text = Loc.T("prices.header.source", source) + Environment.NewLine
                       + Loc.T("prices.header.fetched", result.CheckedAt.ToString("HH:mm:ss"));

        var models = result.Models.Count > 0 ? result.Models : new List<string>();
        var priceColumns = result.Prices.Max(line => line.Prices.Count);
        var columns = Math.Max(priceColumns, models.Count);

        _list.Columns.Add(Loc.T("prices.column.item"), 320);
        _list.Columns.Add(Loc.T("prices.column.tier"), 130);
        for (var index = 0; index < columns; index++)
        {
            var title = index < models.Count ? models[index] : Loc.T("prices.column.price", index + 1);
            _list.Columns.Add(title, 140);
        }

        foreach (var line in result.Prices)
        {
            var item = new ListViewItem(line.Item);
            item.SubItems.Add(line.Tier.Length > 0 ? line.Tier : Loc.T("prices.none"));
            for (var index = 0; index < columns; index++)
            {
                item.SubItems.Add(index < line.Prices.Count ? line.Prices[index] : "");
            }

            _list.Items.Add(item);
        }

        _header.ForeColor = Theme.Colors.Muted;
    }

    private void CopyToClipboard()
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine(string.Join("\t", _list.Columns.Cast<ColumnHeader>().Select(column => column.Text)));
            foreach (ListViewItem item in _list.Items)
            {
                text.AppendLine(string.Join("\t", item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(sub => sub.Text)));
            }

            Clipboard.SetText(text.ToString());
        }
        catch
        {
            // Буфер обмена мог быть занят — это не повод падать.
        }
    }
}
