using System;

namespace JsUnitySync.UnityPackage
{

/// <summary>Unity JsonUtility-compatible mirror of Contracts.Protocol.WireFrame (wire names intentionally camel-case).</summary>
[Serializable]
public sealed class UnityWireFrame
{
    public int kind;
    public string frameId = "";
    public string sessionId = "";
    public string authorId = "";
    public string utcTimestamp = "";
    public long sequence = -1;
    public string payloadJson = "";
}

[Serializable]
public sealed class UnityHelloPayload
{
    public string userId = "";
    public string clientId = "";
    public string sceneGuid = "";
    public string sceneManifestHash = "";
    public string protocolVersion = "2.0";
    public long lastAckedSequence = -1;
}

[Serializable]
public sealed class UnityWelcomePayload
{
    public string protocolVersion = "";
    public string sceneGuid = "";
    public string sceneManifestHash = "";
    public long lastSequence;
    public long replayFromSequence;
}

[Serializable]
public sealed class UnitySceneMutation
{
    public int kind;
    public string objectId = "";
    public string sceneGuid = "";
    public string componentType = "";
    public string propertyName = "";
    public string serializedValue = "";
    public long baseRevision = -1;
    public string dataJson = "{}";
}

[Serializable]
public sealed class UnityEventAckPayload { public long highestAppliedSequence; }
[Serializable]
public sealed class UnityErrorPayload { public string code = ""; public string message = ""; public string detailsJson = ""; }
[Serializable]
public sealed class UnityConflictPayload
{
    public string objectId = "";
    public string componentType = "";
    public string propertyName = "";
    public long acceptedRevision;
    public string acceptedValue = "";
    public string rejectedValue = "";
    public string acceptedById = "";
    public string rejectedById = "";
    public string rejectedEventId = "";
}
[Serializable]
public sealed class UnityLoginRequest { public string email = ""; public string password = ""; }
[Serializable]
public sealed class UnityLoginProfile { public string id = ""; public string email = ""; public string displayName = ""; public long revision; }
[Serializable]
public sealed class UnityLoginResponse { public string token = ""; public string expiresAt = ""; public UnityLoginProfile profile = new UnityLoginProfile(); }
[Serializable]
public sealed class UnitySessionSummary { public string id = ""; public string projectId = ""; public string projectName = ""; public string name = ""; public string sceneGuid = ""; public string sceneManifestHash = ""; public long lastSequence; }

/// <summary>Must stay wire-compatible with JsUnitySync.Domain.Scenes.SceneEventKind.</summary>
public enum UnitySceneEventKind
{
    GameObjectCreated = 0,
    GameObjectDestroyed = 1,
    GameObjectRenamed = 2,
    TransformChanged = 3,
    ComponentAdded = 4,
    ComponentRemoved = 5,
    PropertyChanged = 6,
    SceneLoaded = 7,
    SceneSaved = 8
}

[Serializable]
public sealed class UnitySceneObjectData
{
    public string name = "";
    public string parentObjectId = "";
    public float positionX, positionY, positionZ;
    public float rotationX, rotationY, rotationZ, rotationW = 1;
    public float scaleX = 1, scaleY = 1, scaleZ = 1;
}
}
