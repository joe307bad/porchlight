// A 4x4 matrix and quaternion in doubles, column-major like Three's Matrix4,
// with the handful of operations the town needs (compose, multiply, transform
// points and normals, invert). Engine-free, so model bounds and placements are
// worked out exactly as the TypeScript game works them out.

using System;

namespace Porchlight.Core
{
    public struct Quat
    {
        public double x, y, z, w;

        public Quat(double x, double y, double z, double w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Quat Identity => new Quat(0, 0, 0, 1);

        /// <summary>Quaternion.setFromAxisAngle about +Y (Three's convention).</summary>
        public static Quat AboutY(double angle)
        {
            double half = angle / 2, s = Math.Sin(half);
            return new Quat(0, s, 0, Math.Cos(half));
        }

        public static Quat FromAxisAngle(double ax, double ay, double az, double angle)
        {
            double half = angle / 2, s = Math.Sin(half);
            return new Quat(ax * s, ay * s, az * s, Math.Cos(half));
        }

        /// <summary>Quaternion.slerpFlat, as Three's QuaternionLinearInterpolant uses.</summary>
        public static Quat SlerpFlat(Quat a, Quat b, double t)
        {
            double x0 = a.x, y0 = a.y, z0 = a.z, w0 = a.w;
            double x1 = b.x, y1 = b.y, z1 = b.z, w1 = b.w;
            if (w0 != w1 || x0 != x1 || y0 != y1 || z0 != z1)
            {
                double dot = x0 * x1 + y0 * y1 + z0 * z1 + w0 * w1;
                if (dot < 0)
                {
                    x1 = -x1;
                    y1 = -y1;
                    z1 = -z1;
                    w1 = -w1;
                    dot = -dot;
                }
                double s = 1 - t;
                if (dot < 0.9995)
                {
                    double theta = Math.Acos(dot);
                    double sin = Math.Sin(theta);
                    s = Math.Sin(s * theta) / sin;
                    t = Math.Sin(t * theta) / sin;
                    x0 = x0 * s + x1 * t;
                    y0 = y0 * s + y1 * t;
                    z0 = z0 * s + z1 * t;
                    w0 = w0 * s + w1 * t;
                }
                else
                {
                    x0 = x0 * s + x1 * t;
                    y0 = y0 * s + y1 * t;
                    z0 = z0 * s + z1 * t;
                    w0 = w0 * s + w1 * t;
                    double f = 1 / Math.Sqrt(x0 * x0 + y0 * y0 + z0 * z0 + w0 * w0);
                    x0 *= f;
                    y0 *= f;
                    z0 *= f;
                    w0 *= f;
                }
            }
            return new Quat(x0, y0, z0, w0);
        }
    }

    public sealed class Mat4
    {
        /// <summary>Column-major, as Three stores them.</summary>
        public readonly double[] e = new double[16];

        public Mat4()
        {
            e[0] = e[5] = e[10] = e[15] = 1;
        }

        public Mat4(double[] src)
        {
            Array.Copy(src, e, 16);
        }

        public Mat4 Clone() => new Mat4(e);

        public static Mat4 Translation(double x, double y, double z)
        {
            var m = new Mat4();
            m.e[12] = x;
            m.e[13] = y;
            m.e[14] = z;
            return m;
        }

        public static Mat4 Scale(double x, double y, double z)
        {
            var m = new Mat4();
            m.e[0] = x;
            m.e[5] = y;
            m.e[10] = z;
            return m;
        }

        /// <summary>Matrix4.compose(position, quaternion, scale).</summary>
        public static Mat4 Compose(double px, double py, double pz, Quat q, double sx, double sy, double sz)
        {
            var m = new Mat4();
            var te = m.e;
            double x = q.x, y = q.y, z = q.z, w = q.w;
            double x2 = x + x, y2 = y + y, z2 = z + z;
            double xx = x * x2, xy = x * y2, xz = x * z2;
            double yy = y * y2, yz = y * z2, zz = z * z2;
            double wx = w * x2, wy = w * y2, wz = w * z2;
            te[0] = (1 - (yy + zz)) * sx;
            te[1] = (xy + wz) * sx;
            te[2] = (xz - wy) * sx;
            te[3] = 0;
            te[4] = (xy - wz) * sy;
            te[5] = (1 - (xx + zz)) * sy;
            te[6] = (yz + wx) * sy;
            te[7] = 0;
            te[8] = (xz + wy) * sz;
            te[9] = (yz - wx) * sz;
            te[10] = (1 - (xx + yy)) * sz;
            te[11] = 0;
            te[12] = px;
            te[13] = py;
            te[14] = pz;
            te[15] = 1;
            return m;
        }

        /// <summary>this * b (Matrix4.multiply).</summary>
        public Mat4 Multiply(Mat4 b) => Multiply(this, b);

        public static Mat4 Multiply(Mat4 a, Mat4 b)
        {
            var ae = a.e;
            var be = b.e;
            var m = new Mat4();
            var te = m.e;
            double a11 = ae[0], a12 = ae[4], a13 = ae[8], a14 = ae[12];
            double a21 = ae[1], a22 = ae[5], a23 = ae[9], a24 = ae[13];
            double a31 = ae[2], a32 = ae[6], a33 = ae[10], a34 = ae[14];
            double a41 = ae[3], a42 = ae[7], a43 = ae[11], a44 = ae[15];
            double b11 = be[0], b12 = be[4], b13 = be[8], b14 = be[12];
            double b21 = be[1], b22 = be[5], b23 = be[9], b24 = be[13];
            double b31 = be[2], b32 = be[6], b33 = be[10], b34 = be[14];
            double b41 = be[3], b42 = be[7], b43 = be[11], b44 = be[15];
            te[0] = a11 * b11 + a12 * b21 + a13 * b31 + a14 * b41;
            te[4] = a11 * b12 + a12 * b22 + a13 * b32 + a14 * b42;
            te[8] = a11 * b13 + a12 * b23 + a13 * b33 + a14 * b43;
            te[12] = a11 * b14 + a12 * b24 + a13 * b34 + a14 * b44;
            te[1] = a21 * b11 + a22 * b21 + a23 * b31 + a24 * b41;
            te[5] = a21 * b12 + a22 * b22 + a23 * b32 + a24 * b42;
            te[9] = a21 * b13 + a22 * b23 + a23 * b33 + a24 * b43;
            te[13] = a21 * b14 + a22 * b24 + a23 * b34 + a24 * b44;
            te[2] = a31 * b11 + a32 * b21 + a33 * b31 + a34 * b41;
            te[6] = a31 * b12 + a32 * b22 + a33 * b32 + a34 * b42;
            te[10] = a31 * b13 + a32 * b23 + a33 * b33 + a34 * b43;
            te[14] = a31 * b14 + a32 * b24 + a33 * b34 + a34 * b44;
            te[3] = a41 * b11 + a42 * b21 + a43 * b31 + a44 * b41;
            te[7] = a41 * b12 + a42 * b22 + a43 * b32 + a44 * b42;
            te[11] = a41 * b13 + a42 * b23 + a43 * b33 + a44 * b43;
            te[15] = a41 * b14 + a42 * b24 + a43 * b34 + a44 * b44;
            return m;
        }

        /// <summary>Vector3.applyMatrix4 (with the perspective divide).</summary>
        public void ApplyToPoint(double x, double y, double z, out double ox, out double oy, out double oz)
        {
            var te = e;
            double w = 1 / (te[3] * x + te[7] * y + te[11] * z + te[15]);
            ox = (te[0] * x + te[4] * y + te[8] * z + te[12]) * w;
            oy = (te[1] * x + te[5] * y + te[9] * z + te[13]) * w;
            oz = (te[2] * x + te[6] * y + te[10] * z + te[14]) * w;
        }

        /// <summary>Matrix3.getNormalMatrix: the inverse transpose of the upper 3x3, as 9 values (column-major).</summary>
        public double[] NormalMatrix()
        {
            var me = e;
            double n11 = me[0], n21 = me[1], n31 = me[2];
            double n12 = me[4], n22 = me[5], n32 = me[6];
            double n13 = me[8], n23 = me[9], n33 = me[10];
            double t11 = n33 * n22 - n32 * n23, t12 = n32 * n13 - n33 * n12, t13 = n23 * n12 - n22 * n13;
            double det = n11 * t11 + n21 * t12 + n31 * t13;
            var o = new double[9];
            if (det == 0) return o;
            double detInv = 1 / det;
            // Matrix3.invert then transpose.
            double i0 = t11 * detInv;
            double i1 = (n31 * n23 - n33 * n21) * detInv;
            double i2 = (n32 * n21 - n31 * n22) * detInv;
            double i3 = t12 * detInv;
            double i4 = (n33 * n11 - n31 * n13) * detInv;
            double i5 = (n31 * n12 - n32 * n11) * detInv;
            double i6 = t13 * detInv;
            double i7 = (n21 * n13 - n23 * n11) * detInv;
            double i8 = (n22 * n11 - n21 * n12) * detInv;
            // transpose
            o[0] = i0;
            o[1] = i3;
            o[2] = i6;
            o[3] = i1;
            o[4] = i4;
            o[5] = i7;
            o[6] = i2;
            o[7] = i5;
            o[8] = i8;
            return o;
        }

        public Mat4 Inverse()
        {
            var te = e;
            double n11 = te[0], n21 = te[1], n31 = te[2], n41 = te[3];
            double n12 = te[4], n22 = te[5], n32 = te[6], n42 = te[7];
            double n13 = te[8], n23 = te[9], n33 = te[10], n43 = te[11];
            double n14 = te[12], n24 = te[13], n34 = te[14], n44 = te[15];
            double t11 = n23 * n34 * n42 - n24 * n33 * n42 + n24 * n32 * n43 - n22 * n34 * n43 - n23 * n32 * n44 + n22 * n33 * n44;
            double t12 = n14 * n33 * n42 - n13 * n34 * n42 - n14 * n32 * n43 + n12 * n34 * n43 + n13 * n32 * n44 - n12 * n33 * n44;
            double t13 = n13 * n24 * n42 - n14 * n23 * n42 + n14 * n22 * n43 - n12 * n24 * n43 - n13 * n22 * n44 + n12 * n23 * n44;
            double t14 = n14 * n23 * n32 - n13 * n24 * n32 - n14 * n22 * n33 + n12 * n24 * n33 + n13 * n22 * n34 - n12 * n23 * n34;
            double det = n11 * t11 + n21 * t12 + n31 * t13 + n41 * t14;
            var m = new Mat4();
            if (det == 0)
            {
                Array.Clear(m.e, 0, 16);
                return m;
            }
            double detInv = 1 / det;
            var o = m.e;
            o[0] = t11 * detInv;
            o[1] = (n24 * n33 * n41 - n23 * n34 * n41 - n24 * n31 * n43 + n21 * n34 * n43 + n23 * n31 * n44 - n21 * n33 * n44) * detInv;
            o[2] = (n22 * n34 * n41 - n24 * n32 * n41 + n24 * n31 * n42 - n21 * n34 * n42 - n22 * n31 * n44 + n21 * n32 * n44) * detInv;
            o[3] = (n23 * n32 * n41 - n22 * n33 * n41 - n23 * n31 * n42 + n21 * n33 * n42 + n22 * n31 * n43 - n21 * n32 * n43) * detInv;
            o[4] = t12 * detInv;
            o[5] = (n13 * n34 * n41 - n14 * n33 * n41 + n14 * n31 * n43 - n11 * n34 * n43 - n13 * n31 * n44 + n11 * n33 * n44) * detInv;
            o[6] = (n14 * n32 * n41 - n12 * n34 * n41 - n14 * n31 * n42 + n11 * n34 * n42 + n12 * n31 * n44 - n11 * n32 * n44) * detInv;
            o[7] = (n12 * n33 * n41 - n13 * n32 * n41 + n13 * n31 * n42 - n11 * n33 * n42 - n12 * n31 * n43 + n11 * n32 * n43) * detInv;
            o[8] = t13 * detInv;
            o[9] = (n14 * n23 * n41 - n13 * n24 * n41 - n14 * n21 * n43 + n11 * n24 * n43 + n13 * n21 * n44 - n11 * n23 * n44) * detInv;
            o[10] = (n12 * n24 * n41 - n14 * n22 * n41 + n14 * n21 * n42 - n11 * n24 * n42 - n12 * n21 * n44 + n11 * n22 * n44) * detInv;
            o[11] = (n13 * n22 * n41 - n12 * n23 * n41 - n13 * n21 * n42 + n11 * n23 * n42 + n12 * n21 * n43 - n11 * n22 * n43) * detInv;
            o[12] = t14 * detInv;
            o[13] = (n13 * n24 * n31 - n14 * n23 * n31 + n14 * n21 * n33 - n11 * n24 * n33 - n13 * n21 * n34 + n11 * n23 * n34) * detInv;
            o[14] = (n14 * n22 * n31 - n12 * n24 * n31 - n14 * n21 * n32 + n11 * n24 * n32 + n12 * n21 * n34 - n11 * n22 * n34) * detInv;
            o[15] = (n12 * n23 * n31 - n13 * n22 * n31 + n13 * n21 * n32 - n11 * n23 * n32 - n12 * n21 * n33 + n11 * n22 * n33) * detInv;
            return m;
        }
    }
}
