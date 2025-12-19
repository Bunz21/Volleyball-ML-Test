using UnityEngine;
using Unity.MLAgents;
using System.Collections.Generic;

public class StatsLogger
{
    public static void LogStats(List<VolleyballAgent> agents)
    {
        float blueSum = 0f;
        float redSum = 0f;
        float blueMax = float.MinValue;
        float redMax = float.MinValue;
        float blueHitter = 0f;
        float redHitter = 0f;
        float blueSetter = 0f;
        float redSetter = 0f;
        float bluePasser = 0f;
        float redPasser = 0f;
        int blueCount = 0;
        int redCount = 0;

        foreach (var agent in agents)
        {
            if (agent == null) continue;
            if (agent.teamId == Team.Blue)
            {
                float reward = agent.GetCumulativeReward();
                blueSum += reward;
                blueMax = Mathf.Max(blueMax, reward);

                switch (agent.role)
                {
                    case Role.Hitter:
                        blueHitter += reward;
                        break;
                    case Role.Setter:
                        blueSetter += reward;
                        break;
                    case Role.Passer:
                        bluePasser += reward;
                        break;
                }

                blueCount++;
            }
            else if (agent.teamId == Team.Red)
            {
                float reward = agent.GetCumulativeReward();
                redSum += reward;
                redMax = Mathf.Max(redMax, reward);

                switch (agent.role)
                {
                    case Role.Hitter:
                        redHitter += reward;
                        break;
                    case Role.Setter:
                        redSetter += reward;
                        break;
                    case Role.Passer:
                        redPasser += reward;
                        break;
                }

                redCount++;
            }
        }

        float blueMean = blueCount > 0 ? blueSum / blueCount : 0f;
        float redMean = redCount > 0 ? redSum / redCount : 0f;

        var stats = Academy.Instance.StatsRecorder;

        stats.Add("Team/Blue/MeanReward", blueMean, StatAggregationMethod.MostRecent);
        stats.Add("Team/Red/MeanReward", redMean, StatAggregationMethod.MostRecent);

        stats.Add("Team/Blue/MaxReward", blueMax, StatAggregationMethod.MostRecent);
        stats.Add("Team/Red/MaxReward", redMax, StatAggregationMethod.MostRecent);

        stats.Add("Role/Blue/HitterReward", blueHitter, StatAggregationMethod.MostRecent);
        stats.Add("Role/Blue/SetterReward", blueSetter, StatAggregationMethod.MostRecent);
        stats.Add("Role/Blue/PasserReward", bluePasser, StatAggregationMethod.MostRecent);

        stats.Add("Role/Red/HitterReward", redHitter, StatAggregationMethod.MostRecent);
        stats.Add("Role/Red/SetterReward", redSetter, StatAggregationMethod.MostRecent);
        stats.Add("Role/Red/PasserReward", redPasser, StatAggregationMethod.MostRecent);
    }
}
