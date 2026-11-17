using System;
using System.Collections.Generic;

namespace SolidWorksJoineryAddin.MacroFeatures
{
    [Serializable]
    public struct Point2D
    {
        public double X;
        public double Y;

        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double DistanceTo(Point2D other)
        {
            double dx = X - other.X;
            double dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public override string ToString()
        {
            return string.Format("({0:F4}, {1:F4})", X, Y);
        }
    }

    [Serializable]
    public struct LineSegment2D
    {
        public Point2D Start;
        public Point2D End;

        public LineSegment2D(Point2D start, Point2D end)
        {
            Start = start;
            End = end;
        }
    }

    [Serializable]
    public struct DogboneArc2D
    {
        public Point2D Center;
        public double Radius;
        public Point2D Start;
        public Point2D End;
        public Point2D Mid;

        public DogboneArc2D(Point2D center, double radius, Point2D start, Point2D end, Point2D mid)
        {
            Center = center;
            Radius = radius;
            Start = start;
            End = end;
            Mid = mid;
        }
    }

    [Serializable]
    public class SlotContour2D
    {
        public List<LineSegment2D> Lines { get; set; } = new List<LineSegment2D>();
        public List<DogboneArc2D> Arcs { get; set; } = new List<DogboneArc2D>();
    }

    [Serializable]
    public struct Vector3D
    {
        public double X;
        public double Y;
        public double Z;

        public Vector3D(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public Vector3D Normalized()
        {
            double len = Length;
            if (len < 1e-12) return new Vector3D(0, 0, 0);
            return new Vector3D(X / len, Y / len, Z / len);
        }

        public double Dot(Vector3D other)
        {
            return X * other.X + Y * other.Y + Z * other.Z;
        }

        public Vector3D Cross(Vector3D other)
        {
            return new Vector3D(
                Y * other.Z - Z * other.Y,
                Z * other.X - X * other.Z,
                X * other.Y - Y * other.X);
        }

        public static Vector3D operator +(Vector3D a, Vector3D b) => new Vector3D(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3D operator -(Vector3D a, Vector3D b) => new Vector3D(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3D operator -(Vector3D a) => new Vector3D(-a.X, -a.Y, -a.Z);
        public static Vector3D operator *(Vector3D a, double s) => new Vector3D(a.X * s, a.Y * s, a.Z * s);
        public static Vector3D operator *(double s, Vector3D a) => new Vector3D(a.X * s, a.Y * s, a.Z * s);

        public static void BuildOrthonormalBasis(Vector3D normal, out Vector3D u1, out Vector3D u2)
        {
            Vector3D n = normal.Normalized();
            Vector3D refAxis = Math.Abs(n.X) < 0.8 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
            u1 = (refAxis - n * refAxis.Dot(n)).Normalized();
            u2 = n.Cross(u1).Normalized();
        }

        public override string ToString()
        {
            return string.Format("({0:F4}, {1:F4}, {2:F4})", X, Y, Z);
        }
    }
}
