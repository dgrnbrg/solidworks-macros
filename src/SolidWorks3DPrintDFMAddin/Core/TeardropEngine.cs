using System;
using System.Collections.Generic;

namespace SolidWorks3DPrintDFMAddin.Core
{
    /// <summary>
    /// Represents the 2D cross-section profile of a Teardrop Crown Relief in the circle's (u, v) plane,
    /// where (0, 0) is the circle center, +v points toward the print bed "Up" projection, and +u is horizontal.
    /// </summary>
    public class TeardropProfile2D
    {
        public double Radius { get; set; }
        public double OverhangAngleFromBedDeg { get; set; }
        public double CircleAngleToBedDeg { get; set; }
        public bool IsCapped { get; set; }
        public double EffectiveMaxDeltaV { get; set; }
        public double UncappedDeltaV { get; set; }

        /// <summary>Left tangent point on circle: (-R*sin(alpha), R*cos(alpha))</summary>
        public Point2D TangentLeft { get; set; }
        /// <summary>Right tangent point on circle: (+R*sin(alpha), R*cos(alpha))</summary>
        public Point2D TangentRight { get; set; }
        /// <summary>Top crown midpoint on circle: (0, R) for 3-point arc construction</summary>
        public Point2D ArcMidpoint { get; set; }

        /// <summary>Uncapped pointed apex: (0, R / cos(alpha))</summary>
        public Point2D ApexPoint { get; set; }
        /// <summary>Right flat-top cap corner when IsCapped == true: (+u_cap, v_cap)</summary>
        public Point2D CapRight { get; set; }
        /// <summary>Left flat-top cap corner when IsCapped == true: (-u_cap, v_cap)</summary>
        public Point2D CapLeft { get; set; }

        /// <summary>
        /// Returns the ordered upper roof polyline vertices from TangentRight -> ... -> TangentLeft.
        /// Closing from TangentLeft back to TangentRight via the circular arc (or chord) forms the closed cut loop.
        /// </summary>
        public List<Point2D> GetRoofChain()
        {
            if (IsCapped)
            {
                return new List<Point2D> { TangentRight, CapRight, CapLeft, TangentLeft };
            }
            return new List<Point2D> { TangentRight, ApexPoint, TangentLeft };
        }
    }

    public static class TeardropEngine
    {
        /// <summary>
        /// Computes the angle (in degrees, [0, 90]) between the circle cross-section plane and the print bed plane.
        /// Equal to arccos(|holeAxis . bedUp|).
        /// - 0 deg: circle is parallel to the bed (vertical hole axis) -> no teardrop needed.
        /// - 90 deg: circle is perpendicular to the bed (horizontal hole axis) -> full teardrop needed.
        /// </summary>
        public static double ComputeCircleAngleToBedDeg(Vector3D holeAxis, Vector3D bedUp)
        {
            Vector3D a = holeAxis.Normalized();
            Vector3D b = bedUp.Normalized();
            double dot = Math.Abs(a.Dot(b));
            if (dot > 1.0) dot = 1.0;
            return Math.Acos(dot) * (180.0 / Math.PI);
        }

        /// <summary>
        /// Returns true if a cylindrical hole's circle-to-bed angle qualifies for a teardrop relief:
        /// minCircleAngleToBedDeg (default 40°) &lt;= beta_circle &lt;= 90°.
        /// </summary>
        public static bool QualifiesForTeardrop(
            Vector3D holeAxis,
            Vector3D bedUp,
            double minCircleAngleToBedDeg = 40.0)
        {
            double beta = ComputeCircleAngleToBedDeg(holeAxis, bedUp);
            return beta >= (minCircleAngleToBedDeg - 1e-6) && beta <= 90.0 + 1e-6;
        }

        /// <summary>
        /// Computes the local orthonormal basis (u, v, w) for a cylindrical hole relative to bedUp:
        /// - w: unit vector along holeAxis
        /// - v: unit projection of bedUp perpendicular to holeAxis (points toward upper crown of hole)
        /// - u: v x w (horizontal right in the cross-section plane)
        /// </summary>
        public static bool BuildHoleCrossSectionBasis(
            Vector3D holeAxis,
            Vector3D bedUp,
            out Vector3D uRight,
            out Vector3D vUp,
            out Vector3D wAxis)
        {
            wAxis = holeAxis.Normalized();
            Vector3D b = bedUp.Normalized();
            Vector3D vPerp = b - wAxis * b.Dot(wAxis);
            double vLen = vPerp.Length;
            if (vLen < 1e-8)
            {
                // Hole axis is strictly vertical (circle parallel to bed)
                uRight = new Vector3D(1, 0, 0);
                vUp = new Vector3D(0, 1, 0);
                return false;
            }

            vUp = vPerp * (1.0 / vLen);
            uRight = vUp.Cross(wAxis).Normalized();
            return true;
        }

        /// <summary>
        /// Computes the exact 2D Teardrop Crown Relief profile in the circle's (u, v) cross-section plane.
        /// 
        /// Parameters:
        /// - radius: Circle radius R (meters)
        /// - overhangAngleFromBedDeg: Roof slope angle alpha measured from horizontal/bed (default 40°)
        /// - circleAngleToBedDeg: Angle beta_circle between circle plane and bed plane (40°..90°)
        /// - enableCap: Whether to truncate the apex at maxCapHeightAlongBedNormal
        /// - maxCapHeightAlongBedNormal: Max extension measured along the print bed normal B_hat (default 0.6 mm = 0.0006 m).
        ///   Projected into the cross-section plane via delta_v_max = maxCapHeightAlongBedNormal * sin(beta_circle).
        /// </summary>
        public static TeardropProfile2D ComputeProfile2D(
            double radius,
            double overhangAngleFromBedDeg,
            double circleAngleToBedDeg,
            bool enableCap,
            double maxCapHeightAlongBedNormal)
        {
            if (radius <= 0)
                throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be positive.");

            double alphaDeg = Math.Max(5.0, Math.Min(85.0, overhangAngleFromBedDeg));
            double alphaRad = alphaDeg * (Math.PI / 180.0);

            double betaDeg = Math.Max(1.0, Math.Min(90.0, circleAngleToBedDeg));
            double betaRad = betaDeg * (Math.PI / 180.0);

            double sinA = Math.Sin(alphaRad);
            double cosA = Math.Cos(alphaRad);

            // Tangent points on circle u^2 + v^2 = R^2 for roof sloped at alpha from horizontal:
            // Outward normal is (+/- sin(alpha), cos(alpha))
            Point2D tRight = new Point2D(radius * sinA, radius * cosA);
            Point2D tLeft = new Point2D(-radius * sinA, radius * cosA);
            Point2D arcMid = new Point2D(0.0, radius);

            // Uncapped apex where +/- u*sin(alpha) + v*cos(alpha) = R meets u = 0:
            double vPeak = radius / cosA;
            double uncappedDeltaV = vPeak - radius;
            Point2D apex = new Point2D(0.0, vPeak);

            // Project bed-normal cap height into the circle's cross-section plane:
            // Since v_hat . B_hat = sin(beta_circle), a vertical layer budget delta_h_bed along B_hat
            // corresponds to perpendicular offset delta_v_max = delta_h_bed * sin(beta_circle).
            double sinBeta = Math.Sin(betaRad);
            double effectiveMaxDeltaV = Math.Max(0.0, maxCapHeightAlongBedNormal * sinBeta);
            double vCap = radius + effectiveMaxDeltaV;

            bool isCapped = enableCap && (uncappedDeltaV > effectiveMaxDeltaV + 1e-9) && (effectiveMaxDeltaV > 1e-9);

            Point2D capRight = apex;
            Point2D capLeft = apex;

            if (isCapped)
            {
                // Solve u_cap * sin(alpha) + v_cap * cos(alpha) = R
                double uCap = (radius - vCap * cosA) / sinA;
                if (uCap <= 1e-9)
                {
                    isCapped = false;
                }
                else
                {
                    capRight = new Point2D(uCap, vCap);
                    capLeft = new Point2D(-uCap, vCap);
                }
            }

            return new TeardropProfile2D
            {
                Radius = radius,
                OverhangAngleFromBedDeg = alphaDeg,
                CircleAngleToBedDeg = betaDeg,
                IsCapped = isCapped,
                EffectiveMaxDeltaV = effectiveMaxDeltaV,
                UncappedDeltaV = uncappedDeltaV,
                TangentLeft = tLeft,
                TangentRight = tRight,
                ArcMidpoint = arcMid,
                ApexPoint = apex,
                CapRight = capRight,
                CapLeft = capLeft
            };
        }

        /// <summary>
        /// Maps a 2D point (u, v) in the hole's cross-section plane into 3D model coordinates.
        /// </summary>
        public static Vector3D MapTo3D(Point2D pt2d, Vector3D planeOrigin, Vector3D uRight, Vector3D vUp)
        {
            return planeOrigin + uRight * pt2d.X + vUp * pt2d.Y;
        }
    }
}
