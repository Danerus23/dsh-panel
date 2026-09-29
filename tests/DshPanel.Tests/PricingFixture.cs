// ЗАПИСАННЫЕ СТРАНИЦЫ ЦЕН — данные для проверок разбора, а не выдуманный текст.
//
// ⚠️ СЕТИ В ПРОВЕРКАХ НЕТ ВООБЩЕ: разбор обязан быть проверен по НАСТОЯЩЕЙ странице,
// а живой запрос — это сеть от имени владельца, то есть его право, а не наше. Поэтому
// разбор отдан чистой функции, а страница берётся ЗАПИСАННАЯ: копия снята панелью 1.x
// (`dsh-tray\preview\pricing.html` и `pricing-zh.html`, 24–26.09.2026) и лежит в репозитории
// ровно для этого — «проверять после каждой правки обе версии сразу».
//
// ⚠️ Таблица вырезана из записи ЦЕЛИКОМ, без правок: обрезанная руками запись проверяла бы
// разбор на тексте, которого на странице нет.
namespace DshPanel.Tests;

internal static class PricingFixture
{
    /// <summary>Таблица цен с английской страницы — как она записана.</summary>
    public const string EnglishTable =
        @"<table style=""text-align:center""><tr><td colspan=""3"" style=""text-align:center"">MODEL</td><td>deepseek-flash<sup>(1)</sup></td><td>deepseek-v4-pro<sup>(2)</sup></td></tr><tr><td colspan=""3"">BASE URL (OpenAI Format)</td><td colspan=""2""><a href=""https://api.deepseek.com"" target=""_blank"" rel=""noopener noreferrer"">https://api.deepseek.com</a></td></tr><tr><td colspan=""3"">BASE URL (Anthropic Format)</td><td colspan=""2""><a href=""https://api.deepseek.com/anthropic"" target=""_blank"" rel=""noopener noreferrer"">https://api.deepseek.com/anthropic</a></td></tr><tr><td colspan=""3"" style=""text-align:center"">MODEL VERSION</td><td>DeepSeek-V4.1-Flash</td><td>DeepSeek-V4-Pro-0813</td></tr><tr><td colspan=""3"">THINKING MODE</td><td colspan=""2"">Supports both non-thinking and thinking (default) modes<br>See <a href=""/guides/thinking_mode"">Thinking Mode</a> for how to switch</td></tr><tr><td colspan=""3"">CONTEXT LENGTH</td><td colspan=""2"">1M</td></tr><tr><td colspan=""3"">MAX OUTPUT</td><td colspan=""2"">MAXIMUM: 384K</td></tr><tr><td rowspan=""7"">FEATURES</td><td colspan=""2""><a href=""/guides/json_mode"">Json Output</a></td><td>✓</td><td>✓</td></tr><tr><td colspan=""2""><a href=""/guides/tool_calls"">Tool Calls</a></td><td>✓</td><td>✓</td></tr><tr><td colspan=""2""><a href=""/guides/responses_api"">Responses API</a></td><td>✓</td><td>✓</td></tr><tr><td colspan=""2""><a href=""/guides/anthropic_api"">Anthropic API</a></td><td>✓</td><td>✓</td></tr><tr><td colspan=""2""><a href=""/guides/chat_prefix_completion"">Chat Prefix Completion（Beta）</a></td><td>✓</td><td>✓</td></tr><tr><td colspan=""2""><a href=""/guides/fim_completion"">FIM Completion（Beta）</a></td><td>Non-thinking mode only</td><td>Non-thinking mode only</td></tr><tr><td colspan=""2""><a href=""/guides/vision"">Vision</a></td><td>✓</td><td>Not supported</td></tr><tr><td rowspan=""6"">PRICING<sup>(3)</sup></td><td rowspan=""2"">1M INPUT TOKENS<br>(CACHE HIT)</td><td>OFF-PEAK</td><td>$0.003</td><td>$0.022</td></tr><tr><td>PEAK</td><td>$0.006</td><td>$0.044</td></tr><tr><td rowspan=""2"">1M INPUT TOKENS<br>(CACHE MISS)</td><td>OFF-PEAK</td><td>$0.15</td><td>$0.66</td></tr><tr><td>PEAK</td><td>$0.3</td><td>$1.32</td></tr><tr><td rowspan=""2"">1M OUTPUT TOKENS</td><td>OFF-PEAK</td><td>$0.6</td><td>$1.98</td></tr><tr><td>PEAK</td><td>$1.2</td><td>$3.96</td></tr><tr><td colspan=""3"">Concurrency Limit<sup>(4)</sup></td><td>2500</td><td>500</td></tr></table>";

    /// <summary>Таблица цен с китайской страницы — отдельный документ с числами в юанях.</summary>
    public const string ChineseTable =
        @"<table style=""text-align:center""><tr><td colspan=""3"" style=""text-align:center"">模型</td><td>deepseek-flash<sup>(1)</sup></td><td>deepseek-v4-pro<sup>(2)</sup></td></tr><tr><td colspan=""3"">BASE URL (OpenAI 格式)</td><td colspan=""2""><a href=""https://api.deepseek.com"" target=""_blank"" rel=""noopener noreferrer"">https://api.deepseek.com</a></td></tr><tr><td colspan=""3"">BASE URL (Anthropic 格式)</td><td colspan=""2""><a href=""https://api.deepseek.com/anthropic"" target=""_blank"" rel=""noopener noreferrer"">https://api.deepseek.com/anthropic</a></td></tr><tr><td colspan=""3"" style=""text-align:center"">模型版本</td><td>DeepSeek-V4.1-Flash</td><td>DeepSeek-V4-Pro-0813</td></tr><tr><td colspan=""3"">思考模式</td><td colspan=""2"">支持非思考与思考模式（默认）<br>切换方式详见<a href=""/zh-cn/guides/thinking_mode"">思考模式</a></td></tr><tr><td colspan=""3"">上下文长度</td><td colspan=""2"">1M</td></tr><tr><td colspan=""3"">输出长度</td><td colspan=""2"">最大 384K</td></tr><tr><td rowspan=""7"">功能</td><td colspan=""2""><a href=""/zh-cn/guides/json_mode"">Json Output</a></td><td>支持</td><td>支持</td></tr><tr><td colspan=""2""><a href=""/zh-cn/guides/tool_calls"">Tool Calls</a></td><td>支持</td><td>支持</td></tr><tr><td colspan=""2""><a href=""/zh-cn/guides/responses_api"">Responses API</a></td><td>支持</td><td>支持</td></tr><tr><td colspan=""2""><a href=""/zh-cn/guides/anthropic_api"">Anthropic API</a></td><td>支持</td><td>支持</td></tr><tr><td colspan=""2""><a href=""/zh-cn/guides/chat_prefix_completion"">对话前缀续写（Beta）</a></td><td>支持</td><td>支持</td></tr><tr><td colspan=""2""><a href=""/zh-cn/guides/fim_completion"">FIM 补全（Beta）</a></td><td>仅非思考模式支持</td><td>仅非思考模式支持</td></tr><tr><td colspan=""2""><a href=""/zh-cn/guides/vision"">图像理解</a></td><td>支持</td><td>不支持</td></tr><tr><td rowspan=""6"">价格<sup>(3)</sup></td><td rowspan=""2"">百万tokens输入<br>（缓存命中）</td><td>空闲时段</td><td>0.02元</td><td>0.15元</td></tr><tr><td>高峰时段</td><td>0.04元</td><td>0.30元</td></tr><tr><td rowspan=""2"">百万tokens输入<br>（缓存未命中）</td><td>空闲时段</td><td>1元</td><td>4.5元</td></tr><tr><td>高峰时段</td><td>2元</td><td>9.0元</td></tr><tr><td rowspan=""2"">百万tokens输出</td><td>空闲时段</td><td>4元</td><td>13.5元</td></tr><tr><td>高峰时段</td><td>8元</td><td>27.0元</td></tr><tr><td colspan=""3"">并发限制<sup>(4)</sup></td><td>2500</td><td>500</td></tr></table>";

    /// <summary>Английская фраза про окна пика — вырезана из записи, а не написана заново.</summary>
    public const string EnglishPhrase =
        @"Peak hours are 01:00 - 04:00 and 06:00 - 10:00 UTC, Monday through Friday (all other hours are off-peak). (4) For more d";

    /// <summary>
    /// Китайская фраза про окна пика — в ТОМ ВИДЕ, какой знала панель 1.x: время там
    /// пекинское (UTC+8), и разбор обязан снять восемь часов. Запись страницы этого вида
    /// не содержит вовсе (`pricing-zh.html` — снимок без фразы про часы), поэтому фраза
    /// выписана здесь ДОСЛОВНО из разбора 1.x: там она проверена живым запросом.
    /// </summary>
    public const string ChinesePhrase =
        @"北京时间周一至周五（不含中国法定节假日） 9:00 - 12:00、14:00 - 18:00 为高峰时段";
}
