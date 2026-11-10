using UnityEngine;
using UnityEngine.AI;

namespace MonsterTweaks
{
    /// <summary>敵がプレイヤーに歩いて届くかの判定(ローブ・リーパーで使う)。</summary>
    internal static class Reach
    {
        private static readonly NavMeshPath Path = new NavMeshPath();

        /// <summary>
        /// 歩いてその人に攻撃が届くか。敵のナビメッシュで経路を求め、経路の終わりがその人のすぐそば
        /// (水平 1.5m・高さ 1.3m 以内)まで行けるかを見る。道のない高所や、敵が入れない物の下にいると届かない。
        /// 判断できないときは届くものとする(本来の動きに任せる)。
        /// </summary>
        internal static bool Reachable(Enemy enemy, PlayerAvatar player)
        {
            var nav = enemy ? enemy.NavMeshAgent : null;
            var agent = nav ? nav.Agent : null;
            if (!agent || !player)
            {
                return true;
            }
            var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };
            if (!NavMesh.SamplePosition(enemy.transform.position, out var start, 2f, filter))
            {
                return true;
            }
            Vector3 target = player.transform.position;
            if (!NavMesh.SamplePosition(target, out var end, 2f, filter))
            {
                return false;
            }
            if (!NavMesh.CalculatePath(start.position, end.position, filter, Path) || Path.status == NavMeshPathStatus.PathInvalid
                || Path.corners.Length == 0)
            {
                return false;
            }
            Vector3 last = Path.corners[Path.corners.Length - 1];
            Vector3 flat = new Vector3(last.x - target.x, 0f, last.z - target.z);
            return flat.magnitude <= 1.5f && Mathf.Abs(last.y - target.y) <= 1.3f;
        }

        /// <summary>敵の目からその人が見通せるか(間に壁がない)。</summary>
        internal static bool LineOfSight(Enemy enemy, PlayerAvatar player)
        {
            Vector3 eyes = enemy.Vision ? enemy.Vision.VisionTransform.position : enemy.transform.position + Vector3.up;
            Vector3 target = player.PlayerVisionTarget ? player.PlayerVisionTarget.VisionTransform.position : player.transform.position;
            return !Physics.Linecast(eyes, target, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore);
        }
    }
}
