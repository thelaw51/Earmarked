using System.Text;

namespace Earmarked.Web.Services;

/// <summary>Checks an episode's show notes for a book, so the book page can say the show listed it as well as said it.</summary>
public static class ShowNotes
{
    /// <summary>
    /// True when the notes contain the book's title (the part before any colon) as whole words, ignoring case and
    /// punctuation. A one-word title ("Dune") matches too many notes by chance, so it also needs an author's surname.
    /// </summary>
    public static bool MentionsBook(string? notes, string title, string authors)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return false;
        }

        var colon = title.IndexOf(':', StringComparison.Ordinal);
        var key = Words(colon > 0 ? title[..colon] : title);
        if (key.Length < 4)
        {
            return false;
        }

        var text = $" {Words(notes)} ";
        if (!text.Contains($" {key} ", StringComparison.Ordinal))
        {
            return false;
        }

        return key.Contains(' ', StringComparison.Ordinal)
            || authors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => Words(a).Split(' ')[^1])
                .Any(surname => surname.Length > 1 && text.Contains($" {surname} ", StringComparison.Ordinal));
    }

    /// <summary>Lower-case letters and digits, with every run of anything else made one space.</summary>
    private static string Words(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
            else if (c is '\'' or '’')
            {
                continue;
            }
            else if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().TrimEnd();
    }
}
