using UnityEngine;

namespace JsUnitySync.UnityPackage
{

/// <summary>Serialized per-GameObject collaboration identity. Added only after explicit Editor consent.</summary>
[DisallowMultipleComponent]
[AddComponentMenu("")]
public sealed class JsUnitySyncObjectId : MonoBehaviour
{
    [SerializeField, HideInInspector] private string stableId = string.Empty;
    public string StableId => stableId;

    public void AssignNewId()
    {
        if (string.IsNullOrEmpty(stableId)) stableId = StableIdFactory.NewId();
    }

    public void SetId(string value)
    {
        if (!StableIdFactory.IsValid(value)) throw new System.ArgumentException("Stable object id must be a canonical ULID.", nameof(value));
        stableId = value;
    }
}
}
