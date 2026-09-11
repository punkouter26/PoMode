namespace PoMode.Shared.Analysis;

/// <summary>
/// Whether an outside catalogue's reading of a song matches this app's, for one measurement.
///
/// <para><see cref="Unknown"/> is not a failure — most recordings have no community analysis at all,
/// and the client must show the absence as an absence rather than as a disagreement.</para>
/// </summary>
public enum ReferenceAgreement
{
    Unknown,
    Agrees,
    Differs,
}

/// <summary>
/// What a public music catalogue says about the recording this job appears to be, next to what
/// PoMode measured.
///
/// <para>Two sources, both free and unauthenticated: MusicBrainz for identity (title, artist, release,
/// cover art) and AcousticBrainz for the community's own key and tempo estimates. Neither is treated
/// as truth — the point is the comparison. Agreement is evidence the analysis is right; disagreement
/// is usually the interesting case, because the commonest way to "disagree" with a key is to name the
/// relative minor, which is the exact distinction this app exists to draw.</para>
///
/// <para>Every field past <paramref name="Title"/> is nullable because every one of them is genuinely
/// optional in the upstream data. A match with nothing but a title is still worth showing; a made-up
/// tempo is not.</para>
/// </summary>
public sealed record ReferenceMatchDto(
    string Query,
    string Title,
    string? Artist,
    string? ReleaseTitle,
    string RecordingId,
    string? CoverArtUrl,
    int? LengthSec,
    double MatchConfidence,
    string? ReferenceKey,
    double? ReferenceBpm,
    ReferenceAgreement KeyAgreement,
    ReferenceAgreement TempoAgreement,
    string Comparison);

/// <summary>
/// The answer to a reference lookup: the match, or the reason there is none.
///
/// <para>A separate shape from a bare nullable match so the client can tell "we looked and this
/// recording is not in the catalogue" apart from "the catalogue was unreachable". The first is a fact
/// about the song; the second is a fact about the network, and showing them identically would let a
/// dropped connection read as an obscure recording.</para>
/// </summary>
public sealed record ReferenceLookupDto(
    string Query,
    bool CatalogueReachable,
    ReferenceMatchDto? Match,
    string Summary);
