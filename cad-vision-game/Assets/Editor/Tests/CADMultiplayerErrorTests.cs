using System;
using NUnit.Framework;

/// <summary>Readable reasons for failed host/join and room-anchor steps.</summary>
public class CADMultiplayerErrorTests
{
    [Test]
    public void OnlineFailuresExplainWhatToCheck()
    {
        Assert.That(CADMultiplayerCoordinator.ExplainFailure(new TimeoutException("Creating the room timed out"), false),
            Does.Contain("internet connection"));
        Assert.That(CADMultiplayerCoordinator.ExplainFailure(new Exception("Cannot resolve destination host"), true),
            Does.Contain("can't reach the internet"));
        Assert.That(CADMultiplayerCoordinator.ExplainFailure(new Exception("HTTP/1.1 403 Forbidden"), false),
            Does.Contain("Relay and Sessions"));
        Assert.That(CADMultiplayerCoordinator.ExplainFailure(new Exception("Lobby not found"), true),
            Does.Contain("No room with that code"));
        Assert.That(CADMultiplayerCoordinator.ExplainFailure(new Exception("Lobby is full"), true),
            Does.Contain("full"));
        Assert.That(CADMultiplayerCoordinator.ExplainFailure(new Exception("something odd"), false),
            Is.EqualTo("Could not host room: something odd"));
    }

    [Test]
    public void AnchorFailuresExplainWhatToCheck()
    {
        Assert.That(CADSharedAnchor.Explain("share the room anchor", -2000), Does.Contain("Enhanced Spatial Services"));
        Assert.That(CADSharedAnchor.Explain("share the room anchor", -2001), Does.Contain("Look around the room"));
        Assert.That(CADSharedAnchor.Explain("load the host's room anchor", -2004), Does.Contain("internet"));
        Assert.That(CADSharedAnchor.Explain("save the room anchor", -1), Does.Contain("(-1)"));
    }
}
