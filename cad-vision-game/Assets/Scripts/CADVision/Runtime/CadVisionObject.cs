using UnityEngine;

namespace CADVision
{
    [DisallowMultipleComponent]
    public sealed class CadVisionObject : MonoBehaviour
    {
        public string Id { get; private set; }
        internal void Initialize(string id) => Id = id;
    }
}
