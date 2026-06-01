using System.Collections.Generic;
using System.Net;
using System.Text;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Shared server-side renderer for a message body. Reuses the EXACT hand-written
/// parser the chat UI uses (<see cref="ChatService.ParseBlocks"/> /
/// <see cref="ChatService.ParseSegments"/>) and emits the same dc-* markup, so
/// FORUM topics/replies and FEED posts get identical rich rendering — code fences,
/// blockquotes, inline code, spoilers, @mentions, #hashtags, links, video embeds,
/// and bold/italic/strike/underline — and inherit the global dc-* CSS plus the
/// wasp.js spoiler-reveal / video-lightbox behaviour for free.
///
/// NO System.Text.RegularExpressions (forbidden under NativeAOT TrimMode=full).
/// All user text is HTML-escaped; the only tags emitted are our own, so the
/// result is safe to inject as innerHTML on the client (the feed following-home).
/// </summary>
public static class MessageHtml
{
    public static string Render(string text)
    {
        var sb = new StringBuilder();
        foreach (var b in ChatService.ParseBlocks(text ?? ""))
        {
            if (b.Kind == ChatService.BlockKind.Code)
            {
                sb.Append("<div class=\"dc-codeblock\">");
                if (!string.IsNullOrEmpty(b.Lang))
                    sb.Append("<div class=\"dc-codeblock-bar\"><span class=\"dc-codeblock-lang\">").Append(Esc(b.Lang)).Append("</span></div>");
                sb.Append("<pre class=\"dc-codeblock-pre\"><code>").Append(Esc(b.Text)).Append("</code></pre></div>");
            }
            else if (b.Kind == ChatService.BlockKind.Quote)
            {
                sb.Append("<blockquote class=\"dc-quote\">"); Segs(sb, b.Segments); sb.Append("</blockquote>");
            }
            else
            {
                sb.Append("<div class=\"dc-para\">"); Segs(sb, b.Segments); sb.Append("</div>");
            }
        }
        return sb.ToString();
    }

    private static void Segs(StringBuilder sb, IReadOnlyList<ChatService.Segment> segs)
    {
        foreach (var seg in segs)
        {
            var sc = StyleSuffix(seg);
            if (seg.IsSpoiler)
                sb.Append("<span class=\"dc-spoiler").Append(sc).Append("\" data-wasp-spoiler tabindex=\"0\" role=\"button\" aria-label=\"spoiler, click to reveal\">").Append(Esc(seg.Value)).Append("</span>");
            else if (seg.IsCode)
                sb.Append("<code class=\"dc-code\">").Append(Esc(seg.Value)).Append("</code>");
            else if (seg.IsLink)
            {
                var ve = ChatService.TryVideoEmbed(seg.Value);
                if (ve is not null)
                    sb.Append("<button type=\"button\" class=\"dc-video-card\" data-wasp-video=\"").Append(Esc(ve.Src))
                      .Append("\" data-wasp-video-kind=\"").Append(Esc(ve.Kind)).Append("\" aria-label=\"Play ").Append(Esc(ve.Provider))
                      .Append(" video\"><span class=\"dc-video-play\" aria-hidden=\"true\">▶</span><span class=\"dc-video-meta\"><span class=\"dc-video-label\">")
                      .Append(Esc(ve.Provider)).Append("</span><span class=\"dc-video-url\">").Append(Esc(seg.Value)).Append("</span></span></button>");
                else
                    sb.Append("<a class=\"dc-link").Append(sc).Append("\" href=\"").Append(Esc(seg.Value)).Append("\" target=\"_blank\" rel=\"noopener nofollow\">").Append(Esc(seg.Value)).Append("</a>");
            }
            else if (seg.IsMention)
                sb.Append("<span class=\"dc-mention").Append(sc).Append("\" style=\"").Append(MentionStyle(seg.Value)).Append("\">@").Append(Esc(seg.Value)).Append("</span>");
            else if (seg.IsHashtag)
                sb.Append("<span class=\"dc-tag").Append(sc).Append("\" style=\"").Append(TagStyle(seg.Value)).Append("\">#").Append(Esc(seg.Value)).Append("</span>");
            else if (seg.HasStyle)
                sb.Append("<span class=\"").Append(StyleClass(seg)).Append("\">").Append(Esc(seg.Value)).Append("</span>");
            else
                sb.Append(Esc(seg.Value));
        }
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s ?? "");

    private static int HueFor(string s) { int h = 0; foreach (var c in s ?? "") h = (h * 131 + c) & 0xFFFFFF; return h % 360; }
    private static string MentionStyle(string u) { var h = HueFor(u); return $"color: hsl({h}, 75%, 80%); background: hsla({h}, 60%, 55%, 0.18);"; }
    private static string TagStyle(string t) { var h = HueFor(t); return $"color: hsl({h}, 70%, 78%); background: hsla({h}, 55%, 50%, 0.16);"; }

    private static string StyleClass(ChatService.Segment s)
    {
        var sb = new StringBuilder();
        if (s.Bold) sb.Append("md-b ");
        if (s.Italic) sb.Append("md-i ");
        if (s.Strike) sb.Append("md-s ");
        if (s.Underline) sb.Append("md-u ");
        return sb.ToString().TrimEnd();
    }
    private static string StyleSuffix(ChatService.Segment s) { var c = StyleClass(s); return c.Length == 0 ? "" : " " + c; }
}
