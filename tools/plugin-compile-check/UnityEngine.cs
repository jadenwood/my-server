// Compile-only stub of UnityEngine used by tools/plugin-compile-check/check.sh (type shapes only). Never deployed.
namespace UnityEngine
{
    public class Object
    {
        public static bool operator ==(Object x, Object y) { return ReferenceEquals(x, y); }
        public static bool operator !=(Object x, Object y) { return !ReferenceEquals(x, y); }
        public static implicit operator bool(Object exists) { return !ReferenceEquals(exists, null); }
        public override bool Equals(object o) { return ReferenceEquals(this, o); }
        public override int GetHashCode() { return 0; }
    }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public struct Vector3 { public float x, y, z; }
}
