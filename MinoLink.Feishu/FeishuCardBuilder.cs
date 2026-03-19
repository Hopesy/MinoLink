using FeishuNetSdk.Im.Dtos;
using MinoLink.Core.Interfaces;

namespace MinoLink.Feishu;

/// <summary>
/// 将 <see cref="Card"/> 转换为飞书交互式卡片 DTO。
/// </summary>
internal static class FeishuCardBuilder
{
    public static ElementsCardDto BuildCard(Card card, string? sessionKey = null)
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
                        value = new Dictionary<string, string?>
                        {
                            ["action"] = b.Value,
                            ["session_key"] = sessionKey,
                        },
                    });
                    elements.Add(new
                    {
                        tag = "action",
                        actions = buttons.ToArray(),
                    });
                    break;
            }
        }

        return new ElementsCardDto
        {
            Config = new ElementsCardDto.ConfigSuffix
            {
                EnableForward = true,
            },
            Header = card.Title is not null
                ? new ElementsCardDto.HeaderSuffix
                {
                    Title = new HeaderTitleElement(card.Title, null),
                }
                : null,
            Elements = elements.ToArray(),
        };
    }

    private static string MapButtonStyle(string style) => style switch
    {
        "primary" => "primary",
        "danger" => "danger",
        _ => "default",
    };
}
