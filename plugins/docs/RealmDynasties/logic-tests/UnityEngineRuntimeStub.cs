// Test-only runtime stub of UnityEngine for run.sh. Superset of tools/plugin-compile-check/UnityEngine.cs: adds the
// value types whose converters the Newtonsoft build in Oxide.References.dll initialises when it serializes anything.
// Shapes only; never deployed.
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
    public struct Vector2 { public float x, y; }
    public struct Vector3 { public float x, y, z; }
    public struct Vector4 { public float x, y, z, w; }
    public struct Quaternion { public float x, y, z, w; }
    public struct Color { public float r, g, b, a; }
    public struct Matrix4x4 { public float m00, m01, m02, m03, m10, m11, m12, m13, m20, m21, m22, m23, m30, m31, m32, m33; }
}
