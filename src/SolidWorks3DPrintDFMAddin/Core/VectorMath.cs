using System;

namespace SolidWorks3DPrintDFMAddin.Core
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
    public struct BoundingBox2D
    {
        public double XMin;
        public double XMax;
        public double YMin;
        public double YMax;

        public BoundingBox2D(double xMin, double xMax, double yMin, double yMax)
        {
            XMin = xMin;
            XMax = xMax;
            YMin = yMin;
            YMax = yMax;
        }

        public double Width => XMax - XMin;
        public double Height => YMax - YMin;
        public Point2D Center => new Point2D(0.5 * (XMin + XMax), 0.5 * (YMin + YMax));
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

        /// <summary>
        /// Constructs a deterministic orthonormal in-plane basis (U1, U2) perpendicular to PlaneNormal,
        /// rotated in-plane by angleOffsetDeg.
        /// </summary>
        public static void BuildInPlaneBasis(
            Vector3D planeNormal,
            double angleOffsetDeg,
            out Vector3D u1,
            out Vector3D u2)
        {
            Vector3D n = planeNormal.Normalized();
            // Pick a reference axis least parallel to n
            Vector3D refAxis = Math.Abs(n.X) < 0.8 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
            Vector3D baseU1 = (refAxis - n * refAxis.Dot(n)).Normalized();
            Vector3D baseU2 = n.Cross(baseU1).Normalized();

            double rad = angleOffsetDeg * Math.PI / 180.0;
            double c = Math.Cos(rad);
            double s = Math.Sin(rad);

            u1 = (baseU1 * c + baseU2 * s).Normalized();
            u2 = n.Cross(u1).Normalized();
        }

        public override string ToString()
        {
            return string.Format("({0:F4}, {1:F4}, {2:F4})", X, Y, Z);
        }
    }
}
