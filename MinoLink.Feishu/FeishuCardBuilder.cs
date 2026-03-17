using System.Text.Json;
using MinoLink.Core.Interfaces;

namespace MinoLink.Feishu;

/// <summary>
/// 将 <see cref="Card"/> 转换为飞书交互式卡片 JSON。
/// </summary>
internal static class FeishuCardBuilder
{
    public static string BuildCardJson(Card card)
    {
        var elements = new List<object>();

        foreach (var element in card.Elements)
        {
            switch (element)
            {
                case CardMarkdown md:
                    elements.Add(new
                    {
                        tag = "markdown",
                        content = md.Content,
                    });
                    break;

                case CardDivider:
                    elements.Add(new { tag = "hr" });
                    break;

                case CardActions actions:
                    var buttons = actions.Buttons.Select(b => new
                    {
                        tag = "button",
                        text = new { tag = "plain_text", content = b.Label },
                        type = MapButtonStyle(b.Style),
                        value = new { action = b.Value },
                    });
                    elements.Add(new
                    {
                        tag = "action",
                        actions = buttons.ToArray(),
                    });
                    break;
            }
        }

        var cardObj = new
        {
            config = new { wide_screen_mode = true },
            header = card.Title is not null
                ? new { title = new { tag = "plain_text", content = card.Title } }
                : null,
            elements,
        };

        return JsonSerializer.Serialize(cardObj, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    private static string MapButtonStyle(string style) => style switch
    {
        "primary" => "primary",
        "danger" => "danger",
        _ => "default",
    };
}
