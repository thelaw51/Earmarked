using Earmarked.Core.Data;

namespace Earmarked.Web.Services;

/// <summary>
/// The episode list's <c>?state=…</c> value. Only an <see cref="EpisodeState"/> name counts (any case): numbers and
/// unknown names are ignored, because <c>Enum.TryParse</c> would accept "999" as a state that doesn't exist.
/// </summary>
public static class EpisodeStateParameter
{
    /// <summary>The state named in <paramref name="value"/>, or null when it names none.</summary>
    public static EpisodeState? Parse(string? value) =>
        Enum.GetValues<EpisodeState>()
            .Select(s => (EpisodeState?)s)
            .FirstOrDefault(s => string.Equals(s.ToString(), value?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The relative link to the list of episodes in <paramref name="state"/>, or in any state but Skipped.</summary>
    public static string Link(EpisodeState? state) => state is null ? "episodes" : $"episodes?state={state}";
}
