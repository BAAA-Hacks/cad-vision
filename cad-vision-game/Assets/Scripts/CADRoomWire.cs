using System;
using UnityEngine;

// Serialized over one ordered NGO named-message stream. All poses are parent-local,
// except the model pose, which is relative to the shared spatial anchor.
[Serializable]
public sealed class CADRoomWire
{
    public string kind;
    public int protocol;
    public string packageId;
    public string groupId;
    public string anchorId;
    public string mode;
    public string file;
    public string data;
    public int offset;
    public int glbLength;
    public int jsonLength;
    public string id;
    public string[] ids;
    public string reason;
    public string token;
    public long revision;
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 scale;
    public CADRoomPart[] parts;
}

[Serializable]
public sealed class CADRoomPart
{
    public string id;
    public bool detached;
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 scale;
}
