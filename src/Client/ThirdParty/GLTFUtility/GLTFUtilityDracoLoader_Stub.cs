using UnityEngine;

namespace Siccity.GLTFUtility
{
    /// <summary>
    /// Minimal STUB of GLTFUtility's Draco loader. The real loader needs a native Draco DLL,
    /// which we don't bundle. Our GLB (exported via Blender without Draco) never hits this path,
    /// so this only exists to satisfy the compile-time references in GLTFMesh.cs. If a
    /// Draco-compressed glTF is ever loaded, LoadMesh returns null and the caller logs a warning.
    /// </summary>
    public class GLTFUtilityDracoLoader
    {
        public struct MeshAttributes
        {
            public int pos, norms, uv, joints, weights, col;
            public MeshAttributes(int pos, int norms, int uv, int joints, int weights, int col)
            { this.pos = pos; this.norms = norms; this.uv = uv; this.joints = joints; this.weights = weights; this.col = col; }
        }

        public class AsyncMesh
        {
            public int[] tris;
            public Vector3[] verts;
            public Vector2[] uv;
            public Vector3[] norms;
            public BoneWeight[] boneWeights;
        }

        public AsyncMesh LoadMesh(byte[] encodedData, MeshAttributes attributes) => null;
    }
}
