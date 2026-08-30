using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Services.OffAxis;
using RevitMCPSDK.API.Base;

namespace RevitMCPCommandSet.Commands.OffAxis
{
    /// <summary>
    /// Snaps slightly off-axis walls and structural beams to nearest 0/45/90 orientation.
    /// </summary>
    public class FixOffAxisWallsAndBeamsCommand : ExternalEventCommandBase
    {
        private FixOffAxisWallsAndBeamsEventHandler _handler => (FixOffAxisWallsAndBeamsEventHandler)Handler;

        public override string CommandName => "fix_off_axis_walls_and_beams";

        public FixOffAxisWallsAndBeamsCommand(UIApplication uiApp)
            : base(new FixOffAxisWallsAndBeamsEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var targetIds = new HashSet<int>();
                if (parameters?["elementIds"] != null)
                {
                    if (parameters["elementIds"] is JArray arr)
                    {
                        foreach (var token in arr)
                        {
                            if (int.TryParse(token.ToString(), out int id)) targetIds.Add(id);
                        }
                    }
                    else
                    {
                        string s = parameters["elementIds"].ToString();
                        string[] parts = s.Split(new[] { ',', ';', ' ', '\t', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var p in parts)
                        {
                            if (int.TryParse(p, out int id)) targetIds.Add(id);
                        }
                    }
                }
                _handler.TargetIds = targetIds;

                if (parameters?["minAngleDeg"] != null && double.TryParse(parameters["minAngleDeg"].ToString(), out double minAng))
                    _handler.MinDeviationDeg = minAng;
                else
                    _handler.MinDeviationDeg = 0.0000001;

                if (parameters?["maxAngleDeg"] != null && double.TryParse(parameters["maxAngleDeg"].ToString(), out double maxAng))
                    _handler.MaxDeviationDeg = maxAng;
                else
                    _handler.MaxDeviationDeg = 0.1;

                if (RaiseAndWaitForCompletion(60000))
                {
                    return _handler.Result;
                }
                else
                {
                    throw new TimeoutException("fix_off_axis_walls_and_beams operation timed out");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to fix off-axis walls/beams: {ex.Message}", ex);
            }
        }
    }
}
