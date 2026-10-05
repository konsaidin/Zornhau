// Adapted from Overhaullib-Legacy, copyright (c) 2024 Maltiez, MIT.
// Source revision: 9bb564978967914c2d07fb13a60e5cc1749bb4ee.
// See third-party/overhaul-core/README.md and LICENSE.
using OpenTK.Mathematics;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace WarbandCombat.Native.Colliders;

public readonly struct CuboidFace
{
    public readonly Vector3d VertexA;
    public readonly Vector3d VertexB;
    public readonly Vector3d VertexC;
    public readonly Vector3d VertexD;

    public CuboidFace(Vector4d vertexA, Vector4d vertexB, Vector4d vertexC, Vector4d vertexD)
    {
        VertexA = new(vertexA.X, vertexA.Y, vertexA.Z);
        VertexB = new(vertexB.X, vertexB.Y, vertexB.Z);
        VertexC = new(vertexC.X, vertexC.Y, vertexC.Z);
        VertexD = new(vertexD.X, vertexD.Y, vertexD.Z);
    }

    private double IntersectPlaneWithLine(Vector3d start, Vector3d direction, Vector3d normal)
    {
        double startProjection = Vector3d.Dot(normal, start);
        double directionProjection = Vector3d.Dot(normal, start + direction);
        double planeProjection = Vector3d.Dot(normal, VertexA);

        return (planeProjection - startProjection) / (directionProjection - startProjection);
    }

    public bool Collide(Vector3d segmentStart, Vector3d segmentDirection, out double parameter, out Vector3d intersection)
    {
        Vector3d normal = Vector3d.Cross(VertexB - VertexA, VertexC - VertexA);

        #region Check if segment is parallel to the plane defined by the face
        double denominator = Vector3d.Dot(normal, segmentDirection);
        if (Math.Abs(denominator) < 0.0001f)
        {
            parameter = -1;
            intersection = Vector3d.Zero;
            return false;
        }
        #endregion

        #region Compute intersection point with the plane defined by the face and check if segment intersects the plane
        parameter = IntersectPlaneWithLine(segmentStart, segmentDirection, normal);
        if (parameter < 0 || parameter > 1)
        {
            intersection = Vector3d.Zero;
            return false;
        }
        #endregion

        intersection = segmentStart + parameter * segmentDirection;

        #region Check if the intersection point is within the face boundaries
        Vector3d edge0 = VertexB - VertexA;
        Vector3d vp0 = intersection - VertexA;
        if (Vector3d.Dot(normal, Vector3d.Cross(edge0, vp0)) < 0)
        {
            return false;
        }

        Vector3d edge1 = VertexC - VertexB;
        Vector3d vp1 = intersection - VertexB;
        if (Vector3d.Dot(normal, Vector3d.Cross(edge1, vp1)) < 0)
        {
            return false;
        }

        Vector3d edge2 = VertexD - VertexC;
        Vector3d vp2 = intersection - VertexC;
        if (Vector3d.Dot(normal, Vector3d.Cross(edge2, vp2)) < 0)
        {
            return false;
        }

        Vector3d edge3 = VertexA - VertexD;
        Vector3d vp3 = intersection - VertexD;
        if (Vector3d.Dot(normal, Vector3d.Cross(edge3, vp3)) < 0)
        {
            return false;
        }
        #endregion



        return true;
    }
}

public readonly struct LineCollider
{
    public readonly Vector3d Tail;
    public readonly Vector3d Head;

    public LineCollider(Vector3d tail, Vector3d head)
    {
        Tail = tail;
        Head = head;
    }
}

public readonly struct CuboidAABBCollider
{
    /// <summary>
    /// Min
    /// </summary>
    public readonly Vector3d VertexA;
    /// <summary>
    /// Max
    /// </summary>
    public readonly Vector3d VertexB;

    public Vector3d Center => (VertexA + VertexB) / 2;

    public CuboidAABBCollider(Vector3d vertexA, Vector3d vertexB)
    {
        VertexA = new(Math.Min(vertexA.X, vertexB.X), Math.Min(vertexA.Y, vertexB.Y), Math.Min(vertexA.Z, vertexB.Z));
        VertexB = new(Math.Max(vertexA.X, vertexB.X), Math.Max(vertexA.Y, vertexB.Y), Math.Max(vertexA.Z, vertexB.Z));
    }
    public CuboidAABBCollider(Cuboidf cuboid)
    {
        VertexA = new(Math.Min(cuboid.X1, cuboid.X2), Math.Min(cuboid.Y1, cuboid.Y2), Math.Min(cuboid.Z1, cuboid.Z2));
        VertexB = new(Math.Max(cuboid.X1, cuboid.X2), Math.Max(cuboid.Y1, cuboid.Y2), Math.Max(cuboid.Z1, cuboid.Z2));
    }
    public CuboidAABBCollider(Entity entity)
    {
        Cuboidf collisionBox = entity.CollisionBox.Clone();
        EntityPos position = entity.Pos;
        collisionBox.X1 += (float)position.X;
        collisionBox.Y1 += (float)position.Y;
        collisionBox.Z1 += (float)position.Z;
        collisionBox.X2 += (float)position.X;
        collisionBox.Y2 += (float)position.Y;
        collisionBox.Z2 += (float)position.Z;

        VertexA = new(Math.Min(collisionBox.X1, collisionBox.X2), Math.Min(collisionBox.Y1, collisionBox.Y2), Math.Min(collisionBox.Z1, collisionBox.Z2));
        VertexB = new(Math.Max(collisionBox.X1, collisionBox.X2), Math.Max(collisionBox.Y1, collisionBox.Y2), Math.Max(collisionBox.Z1, collisionBox.Z2));
    }

    public bool Collide(Vector3d segmentStart, Vector3d segmentDirection, out double parameter)
    {
        Vector3d min = Vector3d.ComponentMin(VertexA, VertexB);
        Vector3d max = Vector3d.ComponentMax(VertexA, VertexB);

        parameter = 0;
        double tmin = 0;
        double tmax = 1;

        if (!UpdateSlab(segmentStart.X, segmentDirection.X, min.X, max.X, ref tmin, ref tmax)) return false;
        if (!UpdateSlab(segmentStart.Y, segmentDirection.Y, min.Y, max.Y, ref tmin, ref tmax)) return false;
        if (!UpdateSlab(segmentStart.Z, segmentDirection.Z, min.Z, max.Z, ref tmin, ref tmax)) return false;

        parameter = tmin;
        return true;
    }

    private static bool UpdateSlab(double start, double direction, double min, double max, ref double tmin, ref double tmax)
    {
        if (Math.Abs(direction) <= double.Epsilon)
        {
            return start >= min && start <= max;
        }

        double axisMin = (min - start) / direction;
        double axisMax = (max - start) / direction;
        if (axisMin > axisMax) Swap(ref axisMin, ref axisMax);

        if (axisMin > tmin) tmin = axisMin;
        if (axisMax < tmax) tmax = axisMax;

        return tmin <= tmax;
    }
    public bool Collide(Vector3d origin, double radius, out Vector3d intersection)
    {
        intersection = new(
            Math.Clamp(origin.X, Math.Min(VertexA.X, VertexB.X), Math.Max(VertexA.X, VertexB.X)),
            Math.Clamp(origin.Y, Math.Min(VertexA.Y, VertexB.Y), Math.Max(VertexA.Y, VertexB.Y)),
            Math.Clamp(origin.Z, Math.Min(VertexA.Z, VertexB.Z), Math.Max(VertexA.Z, VertexB.Z))
        );

        double distanceSquared = Vector3d.DistanceSquared(origin, intersection);

        return distanceSquared <= radius * radius;
    }
    public bool Collide(Vector3d thisTickOrigin, Vector3d previousTickOrigin, double radius, out Vector3d intersection)
    {
        if ((thisTickOrigin - previousTickOrigin).LengthSquared < radius / 2)
        {
            bool collided = Collide(thisTickOrigin, radius, out _);

            intersection = thisTickOrigin;

            return collided;
        }

        bool intersects = SegmentIntersectsAABB(previousTickOrigin, thisTickOrigin, VertexA, VertexB, out Vector3d intersectionStart, out Vector3d intersectionEnd, out Vector3d closestPointOnSegment, out Vector3d closestPointOnBox);

        if (intersects)
        {
            intersection = intersectionStart - (intersectionEnd - intersectionStart).Normalized() * radius;
#if DEBUG
            //_api?.World.SpawnParticles(1, ColorUtil.ColorFromRgba(0, 255, 0, 125), new(intersection.X, intersection.Y, intersection.Z), new(intersection.X, intersection.Y, intersection.Z), new Vec3f(), new Vec3f(), 3, 0, 1.0f, EnumParticleModel.Cube);
#endif
            return true;
        }
        else
        {
            intersection = closestPointOnSegment;
        }

        double distanceSquared = Vector3d.DistanceSquared(intersection, closestPointOnBox);
#if DEBUG
        //_api?.World.SpawnParticles(1, ColorUtil.ColorFromRgba(0, 255, 0, 125), new(intersection.X, intersection.Y, intersection.Z), new(intersection.X, intersection.Y, intersection.Z), new Vec3f(), new Vec3f(), 3, 0, 1.0f, EnumParticleModel.Cube);
#endif
        return distanceSquared <= radius * radius;
    }
    public static bool CollideWithTerrain(
        IBlockAccessor blockAccessor,
        Vector3d thisTickOrigin,
        Vector3d previousTickOrigin,
        double radius,
        out Vector3d intersection,
        out Vector3d normal,
        out BlockFacing? facing,
        out Block? block,
        out BlockPos? blockPosition)
    {
        int minX = (int)(Math.Min(thisTickOrigin.X, previousTickOrigin.X) - radius);
        int minY = (int)(Math.Min(thisTickOrigin.Y, previousTickOrigin.Y) - radius);
        int minZ = (int)(Math.Min(thisTickOrigin.Z, previousTickOrigin.Z) - radius);

        int maxX = (int)(Math.Max(thisTickOrigin.X, previousTickOrigin.X) + radius);
        int maxY = (int)(Math.Max(thisTickOrigin.Y, previousTickOrigin.Y) + radius);
        int maxZ = (int)(Math.Max(thisTickOrigin.Z, previousTickOrigin.Z) + radius);

        blockPosition = null;
        BlockPos blockPos = new(0);
        block = null;
        facing = null;
        intersection = new();
        normal = new();
        double shortestDistance = double.MaxValue;
        for (int y = minY; y <= maxY; y++)
        {
            blockPos.SetAndCorrectDimension(minX, y, minZ);
            for (int x = minX; x <= maxX; x++)
            {
                blockPos.X = x;
                for (int z = minZ; z <= maxZ; z++)
                {
                    blockPos.Z = z;
                    Block? blockBuffer = blockAccessor.GetBlock(blockPos, BlockLayersAccess.MostSolid);

                    Cuboidf?[]? collisionBoxes = blockBuffer.GetCollisionBoxes(blockAccessor, blockPos);
                    if (collisionBoxes == null || collisionBoxes.Length == 0) continue;

                    for (int i = 0; i < collisionBoxes.Length; i++)
                    {
                        Cuboidf? collisionBox = collisionBoxes[i];

                        if (collisionBox == null) continue;

                        collisionBox = collisionBox.OffsetCopy(blockPos);

                        CuboidAABBCollider collider = new(collisionBox);

                        if (collider.Collide(thisTickOrigin, previousTickOrigin, radius, out Vector3d currentIntersection))
                        {
                            double currentDistance = (currentIntersection - previousTickOrigin).Length;

                            if (currentDistance < shortestDistance)
                            {
                                intersection = currentIntersection;
                                shortestDistance = currentDistance;
                                block = blockBuffer;
                                blockPosition = blockPos.Copy();
                                facing = collider.GetFacing(intersection - collider.Center, out normal);
                            }
                        }
                    }
                }
            }
        }

        return block != null;
    }

    public BlockFacing GetFacing(Vector3d direction, out Vector3d normal)
    {
        normal = GetIntersectingFaceNormal(VertexA, VertexB, direction);

        return BlockFacing.FromNormal(new Vec3f((float)normal.X, (float)normal.Y, (float)normal.Z));
    }

    private static Vector3d GetIntersectingFaceNormal(Vector3d min, Vector3d max, Vector3d dir)
    {
        // Normalize direction (optional — only needed for consistent t values)
        dir.Normalize();

        Vector3d center = (min + max) * 0.5;
        Vector3d halfExtents = (max - min) * 0.5;

        // Avoid division by zero — use double.MaxValue to represent no intersection in that axis
        double tx = dir.X != 0 ? (Math.Sign(dir.X) * halfExtents.X) / dir.X : double.MaxValue;
        double ty = dir.Y != 0 ? (Math.Sign(dir.Y) * halfExtents.Y) / dir.Y : double.MaxValue;
        double tz = dir.Z != 0 ? (Math.Sign(dir.Z) * halfExtents.Z) / dir.Z : double.MaxValue;

        // Pick smallest positive t
        double t = double.MaxValue;
        Vector3d normal = Vector3d.Zero;

        if (tx > 0 && tx < t) { t = tx; normal = new Vector3d(Math.Sign(dir.X), 0, 0); }
        if (ty > 0 && ty < t) { t = ty; normal = new Vector3d(0, Math.Sign(dir.Y), 0); }
        if (tz > 0 && tz < t) { t = tz; normal = new Vector3d(0, 0, Math.Sign(dir.Z)); }

        return normal;
    }

    private static bool SegmentIntersectsAABB(
        Vector3d p1,
        Vector3d p2,
        Vector3d boxMin,
        Vector3d boxMax,
        out Vector3d intersectionStart,
        out Vector3d intersectionEnd,
        out Vector3d closestPointOnSegment,
        out Vector3d closestPointOnBox)
    {
        intersectionStart = Vector3d.Zero;
        intersectionEnd = Vector3d.Zero;
        closestPointOnSegment = Vector3d.Zero;
        closestPointOnBox = Vector3d.Zero;

        Vector3d dir = p2 - p1;
        Vector3d invDir = new Vector3d(
            1.0 / (dir.X != 0.0 ? dir.X : double.Epsilon),
            1.0 / (dir.Y != 0.0 ? dir.Y : double.Epsilon),
            1.0 / (dir.Z != 0.0 ? dir.Z : double.Epsilon)
        );

        double tmin = (boxMin.X - p1.X) * invDir.X;
        double tmax = (boxMax.X - p1.X) * invDir.X;
        if (tmin > tmax) Swap(ref tmin, ref tmax);

        double tymin = (boxMin.Y - p1.Y) * invDir.Y;
        double tymax = (boxMax.Y - p1.Y) * invDir.Y;
        if (tymin > tymax) Swap(ref tymin, ref tymax);

        if (tmin > tymax || tymin > tmax)
            return ComputeClosestPoints(out closestPointOnSegment, out closestPointOnBox);

        if (tymin > tmin) tmin = tymin;
        if (tymax < tmax) tmax = tymax;

        double tzmin = (boxMin.Z - p1.Z) * invDir.Z;
        double tzmax = (boxMax.Z - p1.Z) * invDir.Z;
        if (tzmin > tzmax) Swap(ref tzmin, ref tzmax);

        if (tmin > tzmax || tzmin > tmax)
            return ComputeClosestPoints(out closestPointOnSegment, out closestPointOnBox);

        if (tzmin > tmin) tmin = tzmin;
        if (tzmax < tmax) tmax = tzmax;

        if (tmax < 0.0 || tmin > 1.0)
            return ComputeClosestPoints(out closestPointOnSegment, out closestPointOnBox);

        double tEnter = Math.Max(0.0, tmin);
        double tExit = Math.Min(1.0, tmax);

        intersectionStart = p1 + dir * tEnter;
        intersectionEnd = p1 + dir * tExit;

        ComputeClosestPoints(out closestPointOnSegment, out closestPointOnBox);

        return true;

        // Closest point fallback
        bool ComputeClosestPoints(out Vector3d closestPointOnSegment, out Vector3d closestPointOnBox)
        {
            // Clamp each coordinate of the segment's closest point to the box
            Vector3d segClosest = ClosestPointOnSegmentToPoint(p1, p2, ClampPointToAABB((p1 + p2) * 0.5, boxMin, boxMax));
            Vector3d boxClosest = ClampPointToAABB(segClosest, boxMin, boxMax);

            closestPointOnSegment = segClosest;
            closestPointOnBox = boxClosest;
            return false;
        }
    }

    private static Vector3d ClosestPointOnSegmentToPoint(Vector3d a, Vector3d b, Vector3d point)
    {
        Vector3d ab = b - a;
        double t = Vector3d.Dot(point - a, ab) / ab.LengthSquared;

        t = Math.Clamp(t, 0.0, 1.0);
        return a + t * ab;
    }

    private static Vector3d ClampPointToAABB(Vector3d point, Vector3d min, Vector3d max)
    {
        return new Vector3d(
            Math.Clamp(point.X, min.X, max.X),
            Math.Clamp(point.Y, min.Y, max.Y),
            Math.Clamp(point.Z, min.Z, max.Z)
        );
    }

    private static void Swap(ref double a, ref double b)
    {
        double tmp = a;
        a = b;
        b = tmp;
    }


}

public sealed class ShapeElementCollider
{
    public const int VertexCount = 8;
    public Vector4d[] ElementVertices { get; } = new Vector4d[VertexCount];
    public Vector4d[] InworldVertices { get; } = new Vector4d[VertexCount];
    // Reused on the game render/tick thread; ShapeElementCollider is not thread-safe.
    private readonly CuboidFace[] _faces = new CuboidFace[6];
    private readonly Vector3d[] _vertexScratch = new Vector3d[VertexCount];
    private readonly Vector3d[] _axesScratch = new Vector3d[3];
    private readonly double[] _halfSizeScratch = new double[3];
    private readonly double[] _transformScratch = new double[16];
    public int JointId { get; set; }

    public EntityShapeRenderer? Renderer { get; set; } = null;
    public bool HasRenderer { get; set; } = false;
    public string ShapeElementName { get; set; } = "";

    public ShapeElementCollider(ShapeElement element)
    {
        JointId = element.JointId;
        SetElementVertices(element);
        ShapeElementName = element.Name ?? "";
    }

    // Use the non-shadow render snapshot and its camera origin. VS 1.22 model
    // matrices are camera-relative; using player feet offsets third-person contacts.
    public void Transform(float[] transformMatrixAll, float[] modelMatrix, Vector3d origin)
    {
        double[] transformMatrix = GetTransformMatrix(JointId, transformMatrixAll);
        for (int vertex = 0; vertex < VertexCount; vertex++)
        {
            var animated = MultiplyVectorByMatrix(transformMatrix, ElementVertices[vertex]);
            animated.W = 1;
            var world = MultiplyVectorByMatrix(modelMatrix, animated);
            world.X += origin.X; world.Y += origin.Y; world.Z += origin.Z;
            InworldVertices[vertex] = world;
        }
    }
    public bool Collide(Vector3d segmentStart, Vector3d segmentDirection, out double parameter, out Vector3d intersection)
    {
        _faces[0] = new CuboidFace(InworldVertices[0], InworldVertices[1], InworldVertices[2], InworldVertices[3]);
        _faces[1] = new CuboidFace(InworldVertices[4], InworldVertices[5], InworldVertices[6], InworldVertices[7]);
        _faces[2] = new CuboidFace(InworldVertices[0], InworldVertices[1], InworldVertices[5], InworldVertices[4]);
        _faces[3] = new CuboidFace(InworldVertices[2], InworldVertices[3], InworldVertices[7], InworldVertices[6]);
        _faces[4] = new CuboidFace(InworldVertices[0], InworldVertices[3], InworldVertices[7], InworldVertices[4]);
        _faces[5] = new CuboidFace(InworldVertices[1], InworldVertices[2], InworldVertices[6], InworldVertices[5]);

        double closestParameter = double.MaxValue;
        bool foundIntersection = false;
        intersection = Vector3d.Zero;

        foreach (CuboidFace face in _faces)
        {
            if (face.Collide(segmentStart, segmentDirection, out double currentParameter, out Vector3d faceIntersection) && currentParameter < closestParameter)
            {
                closestParameter = currentParameter;
                intersection = faceIntersection;
                foundIntersection = true;
            }
        }

        parameter = closestParameter;
        return foundIntersection;
    }
    public bool Collide(Vector3d thisTickOrigin, Vector3d previousTickOrigin, double radius, out double distance, out Vector3d intersection)
    {
        FillVertexScratch();

        intersection = ClosestPoint(thisTickOrigin, previousTickOrigin, _vertexScratch, out Vector3d segmentClosestPoint);
        distance = Vector3d.Distance(intersection, segmentClosestPoint);

        return distance <= radius;
    }
    public bool Collide(Vector3d thisTickOrigin, Vector3d previousTickOrigin, double radius, out double distance, out Vector3d intersection, out Vector3d segmentClosestPoint)
    {
        FillVertexScratch();

        intersection = ClosestPoint(thisTickOrigin, previousTickOrigin, _vertexScratch, out segmentClosestPoint);
        distance = Vector3d.Distance(intersection, segmentClosestPoint);

        return distance <= radius;
    }

    private void FillVertexScratch()
    {
        for (int index = 0; index < VertexCount; index++)
        {
            _vertexScratch[index] = new(InworldVertices[index].X, InworldVertices[index].Y, InworldVertices[index].Z);
        }
    }

    private void SetElementVertices(ShapeElement element)
    {
        // Shader joints transform tessellated, model-space vertices. Use the
        // engine's cached rest transform, which includes parent translations,
        // pivots and scale in block units (shape coordinates are sixteenths).
        var rest = Mat4f.Create();
        Mat4f.Invert(rest, element.inverseModelTransform ?? element.GetInverseModelMatrix());
        var size = new Vector3d((element.To![0] - element.From![0]) / 16,
            (element.To[1] - element.From[1]) / 16, (element.To[2] - element.From[2]) / 16);
        Vector3d[] local = [Vector3d.Zero, new(size.X, 0, 0), new(size.X, size.Y, 0), new(0, size.Y, 0),
            new(0, 0, size.Z), new(size.X, 0, size.Z), size, new(0, size.Y, size.Z)];
        for (int i = 0; i < VertexCount; i++) ElementVertices[i] = MultiplyVectorByMatrix(rest, new(local[i], 1));
    }

    private static int? GetIndex(int jointId, int matrixElementIndex)
    {
        int index = 16 * jointId;
        int offset = matrixElementIndex; /*matrixElementIndex switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            4 => 3,
            5 => 4,
            6 => 5,
            8 => 6,
            9 => 7,
            10 => 8,
            12 => 9,
            13 => 10,
            14 => 11,
            _ => -1
        };*/

        if (offset < 0) return null;

        return index + offset;
    }
    private double[] GetTransformMatrix(int jointId, float[] TransformationMatrices4x4)
    {
        Mat4d.Identity(_transformScratch);
        for (int elementIndex = 0; elementIndex < 16; elementIndex++)
        {
            int? transformMatricesIndex = GetIndex(jointId, elementIndex);
            if (transformMatricesIndex != null)
            {
                if (transformMatricesIndex.Value >= TransformationMatrices4x4.Length)
                {
                    return _transformScratch;
                }

                _transformScratch[elementIndex] = TransformationMatrices4x4[transformMatricesIndex.Value];
            }
        }
        return _transformScratch;
    }
    private static Vector4d MultiplyVectorByMatrix(double[] matrix, Vector4d vector)
    {
        Vector4d result = new(0, 0, 0, 0);
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                result[i] += matrix[4 * j + i] * vector[j];
            }
        }
        return result;
    }
    private static Vector4d MultiplyVectorByMatrix(float[] matrix, Vector4d vector)
    {
        Vector4d result = new(0, 0, 0, 0);
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                result[i] += matrix[4 * j + i] * vector[j];
            }
        }
        return result;
    }
    private Vector3d ClosestPoint(Vector3d start, Vector3d end, Vector3d[] obbVertices, out Vector3d segmentClosestPoint)
    {
        // Assuming the OBB vertices are ordered and form a valid OBB
        // Calculate the center of the OBB
        Vector3d center = (obbVertices[0] + obbVertices[1] + obbVertices[2] + obbVertices[3] +
                          obbVertices[4] + obbVertices[5] + obbVertices[6] + obbVertices[7]) / 8.0f;

        // Calculate the axes of the OBB
        _axesScratch[0] = Vector3d.Normalize(obbVertices[1] - obbVertices[0]); // X-axis
        _axesScratch[1] = Vector3d.Normalize(obbVertices[3] - obbVertices[0]); // Y-axis
        _axesScratch[2] = Vector3d.Normalize(obbVertices[4] - obbVertices[0]); // Z-axis

        // Calculate the half-sizes of the OBB along each axis
        _halfSizeScratch[0] = Vector3d.Distance(obbVertices[0], obbVertices[1]) / 2.0f; // X half-size
        _halfSizeScratch[1] = Vector3d.Distance(obbVertices[0], obbVertices[3]) / 2.0f; // Y half-size
        _halfSizeScratch[2] = Vector3d.Distance(obbVertices[0], obbVertices[4]) / 2.0f; // Z half-size

        // Calculate the closest point on the OBB
        Vector3d closestPoint = center;
        segmentClosestPoint = ClosestPointOnSegment(start, end, center);
        Vector3d direction = segmentClosestPoint - center;

        for (int i = 0; i < 3; i++)
        {
            double distance = Vector3d.Dot(direction, _axesScratch[i]);
            distance = Math.Clamp(distance, -_halfSizeScratch[i], _halfSizeScratch[i]);
            closestPoint += distance * _axesScratch[i];
        }

        return closestPoint;
    }
    public static Vector3d ClosestPointOnSegment(Vector3d A, Vector3d B, Vector3d P)
    {
        Vector3d AB = B - A;
        Vector3d AP = P - A;

        double AB_dot_AB = Vector3d.Dot(AB, AB);
        double AP_dot_AB = Vector3d.Dot(AP, AB);
        double t = AB_dot_AB <= 1e-20 ? 0 : AP_dot_AB / AB_dot_AB;

        // Clamp t to the range [0, 1]
        t = Math.Max(0, Math.Min(1, t));

        // Compute the closest point
        Vector3d closest = A + t * AB;
        return closest;
    }

    public void Render(ICoreClientAPI api, EntityAgent entityPlayer, int color = ColorUtil.WhiteArgb)
    {
        EntityAgent player = api.World.Player.Entity;

        BlockPos playerPos = player.Pos.AsBlockPos;
        Vec3f deltaPos = 0 - new Vec3f(playerPos.X, playerPos.Y, playerPos.Z);

        RenderLine(api, InworldVertices[0], InworldVertices[1], playerPos, deltaPos, color);
        RenderLine(api, InworldVertices[0], InworldVertices[3], playerPos, deltaPos, color);
        RenderLine(api, InworldVertices[0], InworldVertices[4], playerPos, deltaPos, color);

        RenderLine(api, InworldVertices[1], InworldVertices[2], playerPos, deltaPos, color);
        RenderLine(api, InworldVertices[1], InworldVertices[5], playerPos, deltaPos, color);
        RenderLine(api, InworldVertices[2], InworldVertices[6], playerPos, deltaPos, color);
        RenderLine(api, InworldVertices[2], InworldVertices[3], playerPos, deltaPos, color);
        RenderLine(api, InworldVertices[3], InworldVertices[7], playerPos, deltaPos, color);
        RenderLine(api, InworldVertices[4], InworldVertices[7], playerPos, deltaPos, color);
        RenderLine(api, InworldVertices[4], InworldVertices[5], playerPos, deltaPos, color);
        RenderLine(api, InworldVertices[6], InworldVertices[7], playerPos, deltaPos, color);
        RenderLine(api, InworldVertices[6], InworldVertices[5], playerPos, deltaPos, color);
    }

    private static void RenderLine(ICoreClientAPI api, Vector4d start, Vector4d end, BlockPos playerPos, Vec3f deltaPos, int color)
    {
        api.Render.RenderLine(playerPos, (float)start.X + deltaPos.X, (float)start.Y + deltaPos.Y, (float)start.Z + deltaPos.Z, (float)end.X + deltaPos.X, (float)end.Y + deltaPos.Y, (float)end.Z + deltaPos.Z, color);
    }
}
