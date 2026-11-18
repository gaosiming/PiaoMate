using System;
using System.Collections.Generic;
using System.Linq;

namespace PiaoMate
{
    public static class Matcher
    {
        /// <summary>
        /// 从 amounts(分) 中选出一组下标，使合计【不小于】target 且尽可能接近 target（发票要给足，不能少）。
        /// 金额 &lt;= 0 的发票不参与。全部发票合计仍不足 target 时返回空列表。
        /// </summary>
        public static List<int> Find(IList<long> amounts, long target)
        {
            var result = new List<int>();
            if (target <= 0) return result;
            var order = Enumerable.Range(0, amounts.Count).Where(i => amounts[i] > 0).OrderByDescending(i => amounts[i]).ToList();
            if (order.Count == 0) return result;
            long total = order.Sum(i => amounts[i]);
            if (total < target) return result;

            // 最小的"够数"组合，超出部分一定小于最大一张发票金额
            long upper = target + amounts[order[0]];
            if (upper > 400000000L || (double)order.Count * upper > 2.5e9) return Greedy(amounts, order, target);

            int S = (int)upper;
            var first = new int[S + 1];
            for (int i = 0; i <= S; i++) first[i] = -1;
            first[0] = -2;
            foreach (int idx in order)
            {
                int a = (int)Math.Min(amounts[idx], int.MaxValue);
                if (a > S) continue;
                for (int s = S; s >= a; s--)
                    if (first[s] == -1 && first[s - a] != -1) first[s] = idx;
            }
            int best = -1;
            for (int s = (int)target; s <= S; s++) if (first[s] != -1) { best = s; break; }
            if (best < 0) return Greedy(amounts, order, target);
            int cur = best;
            while (cur > 0)
            {
                int idx = first[cur];
                result.Add(idx);
                cur -= (int)amounts[idx];
            }
            return result;
        }

        /// <summary>近似：大额优先凑够，再尝试去掉不必要的小额发票（去掉后仍 ≥ target）。</summary>
        static List<int> Greedy(IList<long> amounts, List<int> order, long target)
        {
            var res = new List<int>(); long sum = 0;
            foreach (int i in order) { res.Add(i); sum += amounts[i]; if (sum >= target) break; }
            foreach (int i in res.OrderBy(i => amounts[i]).ToList())
                if (sum - amounts[i] >= target) { res.Remove(i); sum -= amounts[i]; }
            return res;
        }
    }
}
