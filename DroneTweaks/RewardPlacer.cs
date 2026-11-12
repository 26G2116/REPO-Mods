using System.Collections.Generic;
using UnityEngine;

namespace DroneTweaks
{
    /// <summary>
    /// 報酬のアイテムを出す場所を決める(ホスト)。
    ///
    /// - アイテムの大きさは、プレハブの当たり判定(なければ見た目)から計算する
    /// - 床の上で、壁・物・プレイヤー・敵と重ならず、オーブの位置から壁越しにならない場所を、
    ///   オーブの近くから順に探す
    /// - カートの中に入れるときは、カートの「中に入っている物」を判定する箱(In Cart)の範囲に、
    ///   重ならないように並べる。入りきらないものは床に置く
    /// </summary>
    internal static class RewardPlacer
    {
        private const float SearchStep = 0.35f;
        private const float SearchRadius = 3.5f;
        private const float Gap = 0.04f;

        private static readonly Dictionary<GameObject, Bounds> BoundsCache = new Dictionary<GameObject, Bounds>();

        private static int WallMask => LayerMask.GetMask("Default");

        /// <summary>プレハブの根元から見た大きさ(当たり判定を囲む箱)。</summary>
        internal static Bounds LocalBounds(GameObject prefab)
        {
            if (BoundsCache.TryGetValue(prefab, out var cached))
            {
                return cached;
            }
            var root = prefab.transform;
            bool found = false;
            var result = new Bounds();

            foreach (var part in ColliderBoxes(prefab))
            {
                Encapsulate(ref result, ref found, part.Key, part.Value);
            }

            if (!found)
            {
                foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh && ActiveChain(filter.transform, root))
                    {
                        Encapsulate(ref result, ref found, root.worldToLocalMatrix * filter.transform.localToWorldMatrix, filter.sharedMesh.bounds);
                    }
                }
            }
            if (!found)
            {
                result = new Bounds(Vector3.up * 0.25f, Vector3.one * 0.5f);
            }
            BoundsCache[prefab] = result;
            return result;
        }

        /// <summary>プレハブの当たり判定 1 つずつの「根元から見た変換」と「当たり判定自身の座標での箱」。</summary>
        private static IEnumerable<KeyValuePair<Matrix4x4, Bounds>> ColliderBoxes(GameObject prefab)
        {
            var root = prefab.transform;
            Matrix4x4 toRoot = root.worldToLocalMatrix;
            foreach (var col in prefab.GetComponentsInChildren<Collider>(true))
            {
                if (col.isTrigger || !col.enabled || !ActiveChain(col.transform, root))
                {
                    continue;
                }
                Bounds local;
                switch (col)
                {
                    case BoxCollider box:
                        local = new Bounds(box.center, box.size);
                        break;
                    case SphereCollider sphere:
                        local = new Bounds(sphere.center, Vector3.one * sphere.radius * 2f);
                        break;
                    case CapsuleCollider capsule:
                        var size = Vector3.one * capsule.radius * 2f;
                        size[capsule.direction] = Mathf.Max(capsule.height, capsule.radius * 2f);
                        local = new Bounds(capsule.center, size);
                        break;
                    case MeshCollider mesh when mesh.sharedMesh:
                        local = mesh.sharedMesh.bounds;
                        break;
                    default:
                        continue;
                }
                yield return new KeyValuePair<Matrix4x4, Bounds>(toRoot * col.transform.localToWorldMatrix, local);
            }
        }

        /// <summary>
        /// 立てて置いたときの「床から height までにある部分」の大きさと、それより上の部分の下端の高さ(床から)。
        /// カート武器は砲身がカートの壁より上にあるので、根元の部分だけ場所を取り、砲身の下にも低い物を置ける。
        /// </summary>
        private static Bounds LowerBounds(GameObject prefab, float height, out float overheadClearance)
        {
            var whole = LocalBounds(prefab);
            float floor = whole.min.y;
            bool found = false;
            var lower = new Bounds();
            overheadClearance = float.PositiveInfinity;
            foreach (var part in ColliderBoxes(prefab))
            {
                bool partFound = false;
                var partBounds = new Bounds();
                Encapsulate(ref partBounds, ref partFound, part.Key, part.Value);
                if (partBounds.min.y < floor + height)
                {
                    Encapsulate(ref lower, ref found, Matrix4x4.identity, partBounds);
                }
                else
                {
                    overheadClearance = Mathf.Min(overheadClearance, partBounds.min.y - floor);
                }
            }
            if (!found)
            {
                overheadClearance = 0f;
                return whole;
            }
            return lower;
        }

        private static bool ActiveChain(Transform t, Transform root)
        {
            for (; t; t = t.parent)
            {
                if (!t.gameObject.activeSelf)
                {
                    return false;
                }
                if (t == root)
                {
                    return true;
                }
            }
            return true;
        }

        private static void Encapsulate(ref Bounds result, ref bool found, Matrix4x4 matrix, Bounds local)
        {
            Vector3 min = local.min, max = local.max;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
                var p = matrix.MultiplyPoint3x4(corner);
                if (!found)
                {
                    result = new Bounds(p, Vector3.zero);
                    found = true;
                }
                else
                {
                    result.Encapsulate(p);
                }
            }
        }

        /// <summary>向き(0° か 90°)を回したときの、世界の向きでの大きさ。</summary>
        private static Vector3 RotatedSize(Vector3 size, bool turned)
        {
            return turned ? new Vector3(size.z, size.y, size.x) : size;
        }

        // ---- 床に置く ----

        /// <summary>
        /// 床の上の空いている場所を探す。見つかったら planned に場所を足す。
        /// </summary>
        internal static bool FindFloorSpot(GameObject prefab, Vector3 origin, List<Bounds> planned, out Vector3 position, out Quaternion rotation)
        {
            var local = LocalBounds(prefab);
            int obstacleMask = SemiFunc.LayerMaskGetShouldHits();
            float startAngle = Random.value * Mathf.PI * 2f;

            for (float r = 0f; r <= SearchRadius + 0.001f; r += SearchStep)
            {
                int count = r < 0.01f ? 1 : Mathf.Max(6, Mathf.CeilToInt(Mathf.PI * 2f * r / SearchStep));
                for (int k = 0; k < count; k++)
                {
                    float angle = startAngle + k * Mathf.PI * 2f / count;
                    var xz = origin + new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);
                    if (!Physics.Raycast(new Vector3(xz.x, origin.y + 0.5f, xz.z), Vector3.down, out var floor, 5f, WallMask, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }
                    for (int turn = 0; turn < 2; turn++)
                    {
                        var q = Quaternion.Euler(0f, turn * 90f, 0f);
                        var p = new Vector3(xz.x, floor.point.y + 0.05f - local.min.y, xz.z);
                        var center = p + q * local.center;
                        var half = local.extents + Vector3.one * 0.03f;
                        if (Physics.CheckBox(center, half, q, obstacleMask, QueryTriggerInteraction.Ignore))
                        {
                            continue;
                        }
                        var world = new Bounds(center, RotatedSize(local.size, turn == 1) + Vector3.one * Gap);
                        if (Overlaps(planned, world))
                        {
                            continue;
                        }
                        // オーブの位置から壁越しの場所には出さない
                        if (Physics.Linecast(origin, center, WallMask, QueryTriggerInteraction.Ignore))
                        {
                            continue;
                        }
                        planned.Add(world);
                        position = p;
                        rotation = q;
                        return true;
                    }
                }
            }
            // 見つからなかったときは、オーブの位置のまわりに少しずつずらして出す(同じ場所に重ねない)
            int index = planned.Count;
            float a = index * 2.4f;
            float radius = index == 0 ? 0f : 0.45f + 0.1f * index;
            position = origin + new Vector3(Mathf.Cos(a) * radius, 0.5f, Mathf.Sin(a) * radius);
            rotation = Quaternion.identity;
            planned.Add(new Bounds(position + local.center, local.size + Vector3.one * Gap));
            return false;
        }

        private static bool Overlaps(List<Bounds> planned, Bounds world)
        {
            foreach (var b in planned)
            {
                if (b.Intersects(world))
                {
                    return true;
                }
            }
            return false;
        }

        // ---- カートの中に並べる ----

        internal struct Slot
        {
            internal Item Item;
            internal Vector3 Position;
            internal Quaternion Rotation;
        }

        /// <summary>
        /// カートの中に重ならないように並べる(床の上に 1 段。大きいものから順に、列を作って詰める)。
        /// 入りきらなかったものは remaining に入る。
        /// </summary>
        internal static List<Slot> PackInCart(PhysGrabCart cart, List<Item> items, List<Item> remaining)
        {
            var slots = new List<Slot>();
            var inCart = cart ? cart.inCart : null;
            if (!inCart)
            {
                remaining.AddRange(items);
                return slots;
            }

            // In Cart の箱は X 軸まわりに 90° 傾けて作られている(箱の Y がカートの前後、Z が高さ)。
            // そのまま使うとアイテムが横倒しになるので、カート自身の向きを基準に、箱の大きさを測り直す
            Quaternion boxRotation = cart.transform.rotation;
            Matrix4x4 toCart = Matrix4x4.Rotate(Quaternion.Inverse(boxRotation)) * inCart.localToWorldMatrix;
            Vector3 size = Vector3.zero;
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    size[i] += Mathf.Abs(toCart[i, j]);
                }
            }
            float usableX = size.x - Gap * 2f;
            float usableZ = size.z - Gap * 2f;

            var order = new List<Item>(items);
            order.Sort((a, b) => Footprint(b).CompareTo(Footprint(a)));

            float cursorX = -usableX / 2f, cursorZ = -usableZ / 2f, rowDepth = 0f;
            // カート武器の砲身の下の範囲(箱の座標)と、その下に置ける高さ
            Rect overhead = Rect.zero;
            float overheadClearance = float.PositiveInfinity;

            // カート武器(C.A.R.T. レーザー / キャノン)は、カートの中に置かれていると起動し、カートと同じ向きに立つ(差し込み口はない)。
            // 1 台だけ、起動したときと同じ向き(立てて、砲口がカートの前)でカートの後ろの端(幅の真ん中)に置く。
            // 砲身は壁より上にあるので、根元の部分だけ場所を取る
            var weapon = order.Find(i => i.prefab.Prefab.GetComponent<ItemCartCannonMain>());
            if (weapon)
            {
                order.Remove(weapon);
                var whole = LocalBounds(weapon.prefab.Prefab);
                var lower = LowerBounds(weapon.prefab.Prefab, size.y, out float clearance);
                float zMin = -usableZ / 2f;
                // 根元の箱の中心を、幅の真ん中・後ろの端に合わせる
                var slotLocal = new Vector3(0f, 0f, zMin + lower.size.z / 2f);
                var slotCenter = inCart.position + boxRotation * slotLocal;
                float floorY = CartFloorY(cart, slotCenter, size.y) + 0.05f;
                var offset = boxRotation * lower.center;
                var weaponPosition = new Vector3(slotCenter.x - offset.x, floorY - whole.min.y, slotCenter.z - offset.z);
                if (Obstructed(cart, weaponPosition + boxRotation * whole.center, whole.extents, boxRotation))
                {
                    // 立てるとカートの上や外の物・壁にぶつかる: 床に置く
                    remaining.Add(weapon);
                }
                else
                {
                    slots.Add(new Slot { Item = weapon, Position = weaponPosition, Rotation = boxRotation });
                    // ほかのアイテムは、根元の次の列から並べる
                    cursorZ = zMin + lower.size.z + Gap;
                    // 砲身の下(根元より前)は、砲身の下端より低い物だけ置ける
                    float shiftZ = slotLocal.z - lower.center.z;
                    overhead = Rect.MinMaxRect(whole.min.x - lower.center.x, whole.min.z + shiftZ, whole.max.x - lower.center.x, whole.max.z + shiftZ);
                    overheadClearance = clearance;
                }
            }
            foreach (var item in order)
            {
                var local = LocalBounds(item.prefab.Prefab);
                bool placed = false;
                bool tall = local.size.y > overheadClearance - Gap;
                // 今の列に入る向きを先に試し、だめなら次の列へ。砲身より高い物は、最後に砲身より前の列も試す
                for (int pass = 0; pass < 3 && !placed; pass++)
                {
                    if (pass == 2 && !tall)
                    {
                        break;
                    }
                    for (int turn = 0; turn < 2 && !placed; turn++)
                    {
                        var footprint = RotatedSize(local.size, turn == 1);
                        float x = cursorX, z = cursorZ, depth = rowDepth;
                        if (pass >= 1)
                        {
                            x = -usableX / 2f;
                            z = cursorZ + rowDepth + Gap;
                            depth = 0f;
                        }
                        if (pass == 2)
                        {
                            z = Mathf.Max(z, overhead.yMax + Gap);
                        }
                        // 砲身より高い物が砲身の下にかかるときは、砲身の右側へずらす
                        if (tall && overhead.Overlaps(Rect.MinMaxRect(x, z, x + footprint.x, z + footprint.z)))
                        {
                            x = Mathf.Max(x, overhead.xMax + Gap);
                        }
                        if (x + footprint.x > usableX / 2f + 0.001f || z + footprint.z > usableZ / 2f + 0.001f)
                        {
                            continue;
                        }

                        var yaw = Quaternion.Euler(0f, turn * 90f, 0f);
                        var rotation = boxRotation * yaw;
                        var slotCenter = inCart.position + boxRotation * new Vector3(x + footprint.x / 2f, 0f, z + footprint.z / 2f);
                        float floorY = CartFloorY(cart, slotCenter, size.y) + 0.03f;
                        // 当たり判定の箱の中心が、並べた場所の真ん中に来るように根元の位置をずらす
                        var offset = rotation * local.center;
                        var position = new Vector3(slotCenter.x - offset.x, floorY - local.min.y, slotCenter.z - offset.z);
                        if (Obstructed(cart, position + offset, local.extents, rotation))
                        {
                            continue; // カートからはみ出した部分が、まわりの物や壁にめり込む
                        }

                        slots.Add(new Slot { Item = item, Position = position, Rotation = rotation });
                        cursorX = x + footprint.x + Gap;
                        cursorZ = z;
                        rowDepth = Mathf.Max(depth, footprint.z);
                        placed = true;
                    }
                }
                if (!placed)
                {
                    remaining.Add(item);
                }
            }
            return slots;
        }

        /// <summary>カート自身以外の壁・物・人・敵と重なるか。</summary>
        private static bool Obstructed(PhysGrabCart cart, Vector3 center, Vector3 half, Quaternion rotation)
        {
            foreach (var col in Physics.OverlapBox(center, half + Vector3.one * 0.01f, rotation, SemiFunc.LayerMaskGetShouldHits(), QueryTriggerInteraction.Ignore))
            {
                if (col.GetComponentInParent<PhysGrabCart>() != cart)
                {
                    return true;
                }
            }
            return false;
        }

        private static float Footprint(Item item)
        {
            var size = LocalBounds(item.prefab.Prefab).size;
            return size.x * size.z;
        }

        /// <summary>カートの底の高さ。上から下へ光線を飛ばして、最初に当たったカート自身の当たり判定を使う。</summary>
        private static float CartFloorY(PhysGrabCart cart, Vector3 slotCenter, float boxHeight)
        {
            var from = slotCenter + Vector3.up * (boxHeight / 2f + 0.3f);
            var hits = Physics.RaycastAll(from, Vector3.down, boxHeight + 1.5f, SemiFunc.LayerMaskGetVisionObstruct(), QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            float bestDistance = float.PositiveInfinity;
            foreach (var hit in hits)
            {
                if (hit.distance < bestDistance && hit.collider.GetComponentInParent<PhysGrabCart>() == cart)
                {
                    bestDistance = hit.distance;
                    best = hit.point.y;
                }
            }
            return float.IsNegativeInfinity(best) ? slotCenter.y - boxHeight / 2f : best;
        }
    }
}
