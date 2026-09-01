using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Dwg
{
    /// <summary>
    /// Read-only handler that extracts curve geometry from an imported (ImportInstance)
    /// or linked (RevitLinkInstance over a CADLinkType) DWG file.
    /// </summary>
    public class ExtractDwgCurvesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public string DwgNameOrId { get; set; }
        public string LayerFilter { get; set; }
        public int MaxCurves { get; set; } = 500;

        public object Result { get; private set; }

        public bool WaitForCompletion(int timeoutMilliseconds = 60000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                var doc = app.ActiveUIDocument?.Document;
                if (doc == null)
                {
                    Result = new Dictionary<string, object> { ["error"] = "No active document" };
                    return;
                }

                var candidates = CollectDwgCandidates(doc);

                // List mode: no target specified
                if (string.IsNullOrWhiteSpace(DwgNameOrId))
                {
                    var list = new List<Dictionary<string, object>>();
                    foreach (var c in candidates)
                        list.Add(new Dictionary<string, object> { ["id"] = c.Id, ["name"] = c.Name, ["kind"] = c.Kind });
                    Result = new Dictionary<string, object>
                    {
                        ["mode"] = "list",
                        ["count"] = list.Count,
                        ["dwgs"] = list
                    };
                    return;
                }

                // Resolve target by id or name (exact, then contains)
                Element target = null;
                string targetKind = null;
                string targetName = null;

                if (long.TryParse(DwgNameOrId.Trim(), out long idVal))
                {
                    var c = candidates.FirstOrDefault(x => x.Id == idVal);
                    if (c != null)
                    {
                        target = c.Element;
                        targetKind = c.Kind;
                        targetName = c.Name;
                    }
                }

                if (target == null)
                {
                    var q = DwgNameOrId.Trim();
                    var c = candidates.FirstOrDefault(x => string.Equals(x.Name, q, StringComparison.OrdinalIgnoreCase))
                         ?? candidates.FirstOrDefault(x => x.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (c != null)
                    {
                        target = c.Element;
                        targetKind = c.Kind;
                        targetName = c.Name;
                    }
                }

                if (target == null)
                {
                    Result = new Dictionary<string, object>
                    {
                        ["error"] = $"No imported or linked DWG matching '{DwgNameOrId}'",
                        ["available"] = candidates.Select(x => new Dictionary<string, object> { ["id"] = x.Id, ["name"] = x.Name, ["kind"] = x.Kind }).ToList()
                    };
                    return;
                }

                // Extract
                var curves = new List<Dictionary<string, object>>();
                var layerCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int totalCurves = 0;
                bool truncated = false;
                string layerF = string.IsNullOrWhiteSpace(LayerFilter) ? null : LayerFilter.Trim();

                double[] P(XYZ p) => new double[] { p.X, p.Y, p.Z };

                string LayerName(GeometryObject o)
                {
                    try
                    {
                        var gs = doc.GetElement(o.GraphicsStyleId) as GraphicsStyle;
                        return gs != null && gs.GraphicsStyleCategory != null ? gs.GraphicsStyleCategory.Name : "";
                    }
                    catch { return ""; }
                }

                void Walk(GeometryElement ge)
                {
                    foreach (var o in ge)
                    {
                        if (o is GeometryInstance ngi)
                        {
                            var inst = ngi.GetInstanceGeometry();
                            if (inst != null) Walk(inst);
                        }
                        else if (o is Curve cv)
                        {
                            string layer = LayerName(cv);
                            if (layer.IndexOf("Sketch", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            if (layerF != null && !string.Equals(layer, layerF, StringComparison.OrdinalIgnoreCase)) continue;

                            totalCurves++;
                            if (layerCounts.ContainsKey(layer)) layerCounts[layer]++; else layerCounts[layer] = 1;
                            if (curves.Count >= MaxCurves) { truncated = true; continue; }

                            try
                            {
                                var j = new Dictionary<string, object>();
                                j["type"] = cv.GetType().Name;
                                j["layer"] = layer;
                                var tess = new List<double[]>();
                                foreach (var p in cv.Tessellate()) tess.Add(P(p));
                                j["tessellated"] = tess;

                                bool hasEnds = false; XYZ s = null, e = null;
                                try { s = cv.GetEndPoint(0); e = cv.GetEndPoint(1); hasEnds = true; } catch { }
                                if (hasEnds)
                                {
                                    double len = 0; try { len = cv.ApproximateLength; } catch { }
                                    j["length"] = len;
                                    j["start"] = P(s);
                                    j["end"] = P(e);
                                    XYZ dir = e - s;
                                    double dl = dir.GetLength();
                                    if (dl > 1e-9) j["direction"] = new double[] { dir.X / dl, dir.Y / dl, dir.Z / dl };
                                }

                                if (cv is Arc arc)
                                {
                                    j["center"] = P(arc.Center);
                                    j["radius"] = arc.Radius;
                                    XYZ n = arc.Normal;
                                    j["normal"] = new double[] { n.X, n.Y, n.Z };
                                    j["isFullCircle"] = !hasEnds;
                                }
                                else if (cv is Ellipse el)
                                {
                                    j["center"] = P(el.Center);
                                    j["radiusX"] = el.RadiusX;
                                    j["radiusY"] = el.RadiusY;
                                    XYZ n2 = el.Normal;
                                    j["normal"] = new double[] { n2.X, n2.Y, n2.Z };
                                    j["isFullEllipse"] = !hasEnds;
                                }

                                curves.Add(j);
                            }
                            catch (Exception ex)
                            {
                                curves.Add(new Dictionary<string, object>
                                {
                                    ["type"] = cv.GetType().Name,
                                    ["layer"] = layer,
                                    ["error"] = ex.Message
                                });
                            }
                        }
                    }
                }

                var options = new Options();
                var geo = target.get_Geometry(options);
                if (geo != null) Walk(geo);

                var layerArr = new List<Dictionary<string, object>>();
                foreach (var kv in layerCounts.OrderBy(k => k.Key))
                    layerArr.Add(new Dictionary<string, object> { ["layer"] = kv.Key, ["count"] = kv.Value });

                Result = new Dictionary<string, object>
                {
                    ["mode"] = "extract",
                    ["source"] = new Dictionary<string, object>
                    {
                        ["id"] = IdValue(target),
                        ["name"] = targetName,
                        ["kind"] = targetKind
                    },
                    ["layerFilter"] = layerF,
                    ["totalCurves"] = totalCurves,
                    ["returnedCurves"] = curves.Count,
                    ["truncated"] = truncated,
                    ["layerCount"] = layerCounts.Count,
                    ["layers"] = layerArr,
                    ["curves"] = curves
                };
            }
            catch (Exception ex)
            {
                Result = new Dictionary<string, object> { ["error"] = ex.Message, ["stack"] = ex.StackTrace };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private static long IdValue(Element e)
        {
#if REVIT2024_OR_GREATER
            return e.Id.Value;
#else
            return e.Id.IntegerValue;
#endif
        }

        private List<DwgCandidate> CollectDwgCandidates(Document doc)
        {
            var result = new List<DwgCandidate>();

            // Imported DWGs
            foreach (ImportInstance ii in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)))
            {
                string name = ii.Category?.Name ?? ii.Name;
                result.Add(new DwgCandidate { Element = ii, Id = IdValue(ii), Name = name, Kind = "imported" });
            }

            // Linked CAD DWGs (RevitLinkInstance whose type is a CADLinkType)
            foreach (RevitLinkInstance rli in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
            {
                var lt = doc.GetElement(rli.GetTypeId()) as CADLinkType;
                if (lt == null) continue;
                result.Add(new DwgCandidate { Element = rli, Id = IdValue(rli), Name = lt.Name, Kind = "linked" });
            }

            return result;
        }

        public string GetName()
        {
            return "Extract DWG Curves";
        }

        private class DwgCandidate
        {
            public Element Element;
            public long Id;
            public string Name;
            public string Kind;
        }
    }
}
