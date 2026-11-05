using System.Collections.Generic;
using PoseKit.Bones;

namespace PoseKit.Library;

/// <summary>Two body parts measured close together during a play.</summary>
public readonly record struct PartContact(BodyPart A, BodyPart B, float Distance);

/// <summary>
/// What body contact says about a couple. Contact beats author codes because it records what the
/// animation actually does: rows the Nightlife+ codes call MxF were aligned Vagina to Mouth.
/// </summary>
public static class ContactEvidence
{
    /// How close counts as touching when measuring, in yalms. A first estimate; measurements only
    /// ever suggest a tag, so a poor value can't mislabel anything by itself.
    public const float TouchingDistance = 0.15f;

    /// The tag an aligned contact declares. A penis means MxF, a vagina without one means FxF, and
    /// anything else (mouth to mouth, a hand) suits either couple.
    public static CouplePairing FromAlignedParts(bool penis, bool vagina) =>
        penis ? CouplePairing.MxF : vagina ? CouplePairing.FxF : CouplePairing.Either;

    /// Combines alignments of one animation, such as its two sides.
    public static CouplePairing? FromAlignments(IEnumerable<(BodyPart Self, BodyPart Partner)> alignments)
    {
        bool any = false, penis = false, vagina = false;
        foreach (var (self, partner) in alignments)
        {
            any = true;
            penis |= self == BodyPart.Penis || partner == BodyPart.Penis;
            vagina |= self == BodyPart.Vagina || partner == BodyPart.Vagina;
        }
        return any ? FromAlignedParts(penis, vagina) : null;
    }

    /// The tag the closest touching pair suggests, or null when nothing tells. Only the closest pair
    /// counts: the penis bones sit at the crotch on every skeleton, so a mouth at a vagina is also
    /// near a penis bone even when no penis is drawn.
    public static CouplePairing? Suggest(IEnumerable<PartContact> contacts)
    {
        PartContact? closest = null;
        foreach (var contact in contacts)
        {
            if (contact.Distance <= TouchingDistance && (closest is not { } best || contact.Distance < best.Distance))
                closest = contact;
        }
        if (closest is not { } pair) return null;

        if (pair.A == BodyPart.Penis || pair.B == BodyPart.Penis) return CouplePairing.MxF;
        if (pair.A == BodyPart.Vagina || pair.B == BodyPart.Vagina) return CouplePairing.FxF;
        return pair.A == BodyPart.Mouth && pair.B == BodyPart.Mouth ? CouplePairing.Either : null;
    }
}
