using CADEN.Unity;
using Oculus.Interaction.Input;
using UnityEngine;

/// <summary>
/// CADEN voice for tracked hands: a middle-finger pinch (thumb to middle finger) on either hand
/// is the hand version of the left controller's Y, driving the same CadenVoiceInput
/// press/release: pinch and hold to talk (letting go sends), a quick pinch starts and another
/// sends, a pinch while CADEN is working cancels. Both hands work the same way; while one hand
/// holds a pinch, the other hand's pinches are ignored.
///
/// The index pinch stays select (rays), and Meta's system gesture is a palm-up index pinch, so
/// the middle pinch collides with neither. A pinch is ignored (until released) while that
/// hand's index finger is also pinching (a fist), while that hand is pointing/dragging
/// something, right after its previous pinch, or while CADEN is off (Main Menu toggle). If
/// tracking drops while a pinch is held, recording continues: the next pinch sends (never half
/// a sentence).
/// </summary>
public sealed class CadenHandVoice : MonoBehaviour
{
    private const float SearchInterval = 1f;

    private static readonly Handedness[] Sides = { Handedness.Left, Handedness.Right };
    private readonly IHand[] hands = new IHand[2];
    private readonly CadenPinchToTalk[] pinches = { new CadenPinchToTalk(), new CadenPinchToTalk() };
    private CadenVoiceInput voice;
    private CadenUnityHost host;
    private CADUISettings settings;
    private CADPointerInteraction pointer;
    private float nextSearch;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindAnyObjectByType<CadenHandVoice>() == null)
            new GameObject("CADEN Hand Voice").AddComponent<CadenHandVoice>();
    }

    private void Update()
    {
        Discover();
        bool ready = (settings == null || settings.CadenEnabled) && voice != null && host != null;
        string owner = pointer != null ? pointer.OwnerSourceId : null;

        for (int side = 0; side < 2; side++)
        {
            IHand hand = hands[side];
            bool tracked = hand != null && hand.IsConnected && hand.IsTrackedDataValid;
            bool middle = tracked && hand.GetFingerIsPinching(HandFinger.Middle);
            bool index = tracked && hand.GetFingerIsPinching(HandFinger.Index);
            bool busy = owner != null && owner.StartsWith(side == 0 ? "Left Hand" : "Right Hand");
            bool otherHeld = pinches[1 - side].IsHeld;

            switch (pinches[side].Update(tracked, middle, index, ready && !otherHeld, busy, Time.unscaledTime))
            {
                case CadenPinchToTalk.Action.Press:
                    voice.Press(host);
                    break;
                case CadenPinchToTalk.Action.Release:
                    voice?.Release();
                    break;
            }
        }
    }

    // Each hand's HandRef (the rig's hand interactors), CADEN's voice input and host.
    private void Discover()
    {
        if (Time.unscaledTime < nextSearch)
            return;
        nextSearch = Time.unscaledTime + SearchInterval;

        for (int side = 0; side < 2; side++)
        {
            if (hands[side] != null && hands[side].IsConnected)
                continue;
            foreach (HandRef hand in FindObjectsByType<HandRef>(FindObjectsInactive.Include))
            {
                try
                {
                    if (hand.Handedness == Sides[side])
                    {
                        hands[side] = hand;
                        if (hand.IsConnected)
                            break;
                    }
                }
                catch (System.NullReferenceException) { } // Ref not initialized yet.
            }
        }

        if (voice == null) voice = FindAnyObjectByType<CadenVoiceInput>();
        if (host == null) host = FindAnyObjectByType<CadenUnityHost>();
        if (settings == null) settings = FindAnyObjectByType<CADUISettings>();
        if (pointer == null) pointer = FindAnyObjectByType<CADPointerInteraction>();
    }
}

/// <summary>
/// Pinch → voice press/release decisions for CadenHandVoice (input-free, so it can be tested).
/// </summary>
public sealed class CadenPinchToTalk
{
    public enum Action { None, Press, Release }

    /// <summary>Shortest time between two pinches that both count (s).</summary>
    public float MinInterval = 0.2f;

    private bool held;             // A counted pinch is held (its release goes to the voice).
    private bool ignoreUntilOpen = true; // A pinch that doesn't count, one held across tracking loss, or held at start.
    private float lastPress = float.NegativeInfinity;

    public bool IsHeld => held;

    public Action Update(bool tracked, bool middlePinching, bool indexPinching, bool enabled, bool handBusy, float time)
    {
        if (!tracked)
        {
            // Keep recording: no release. The pinch has to open before it counts again.
            held = false;
            ignoreUntilOpen = true;
            return Action.None;
        }

        if (ignoreUntilOpen)
        {
            if (!middlePinching)
                ignoreUntilOpen = false;
            return Action.None;
        }

        if (!held && middlePinching)
        {
            if (!enabled || indexPinching || handBusy || time - lastPress < MinInterval)
            {
                ignoreUntilOpen = true;
                return Action.None;
            }
            held = true;
            lastPress = time;
            return Action.Press;
        }

        if (held && !middlePinching)
        {
            held = false;
            return Action.Release;
        }

        return Action.None;
    }
}
