using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Services.Dwg
{
    /// <summary>
    /// Shared DWG interrogation primitives used by the DwgCommandSet handlers:
    /// resolves imported/linked DWGs by id or name, and collects the curves of a
    /// given layer in world coordinates by recursively walking instance geometry.
    /// </summary>
    public static class DwgCurveSource
    {
        public class DwgCandidate
        {
            public Element Element;
            public long Id;
            public string Name;
            public string Kind;
        }

        public static long IdValue(Element e)
        {
#if REVIT2024_OR_GREATER
            return e.Id.Value;
#else
            return e.Id.IntegerValue;
#endif
        }

        public static string LayerName(Document doc, GeometryObject o)
        {
            try
            {
                var gs = doc.GetElement(o.GraphicsStyleId) as GraphicsStyle;
                return gs != null && gs.GraphicsStyleCategory != null ? gs.GraphicsStyleCategory.Name : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// All imported (ImportInstance) and linked CAD (RevitLinkInstance over
        /// CADLinkType) DWGs in the document.
        /// </summary>
        public static List<DwgCandidate> CollectCandidates(Document doc)
        {
            var result = new List<DwgCandidate>();

            foreach (ImportInstance ii in new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)))
            {
                string name = ii.Category?.Name ?? ii.Name;
                result.Add(new DwgCandidate { Element = ii, Id = IdValue(ii), Name = name, Kind = "imported" });
            }

            foreach (RevitLinkInstance rli in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
            {
                var lt = doc.GetElement(rli.GetTypeId()) as CADLinkType;
                if (lt == null) continue;
                result.Add(new DwgCandidate { Element = rli, Id = IdValue(rli), Name = lt.Name, Kind = "linked" });
            }

            return result;
        }

        /// <summary>
        /// Resolve a DWG element by element id, exact name, or partial name.
        /// </summary>
        public static Element ResolveDwg(Document doc, string query, out string name, out string kind)
        {
            query = (query ?? "").Trim();
            name = null; kind = null;
            var candidates = CollectCandidates(doc);

            if (query.Length == 0) return null;

            if (long.TryParse(query, out long idVal))
            {
                var byId = candidates.FirstOrDefault(x => x.Id == idVal);
                if (byId != null) { name = byId.Name; kind = byId.Kind; return byId.Element; }
            }

            var byName = candidates.FirstOrDefault(x => string.Equals(x.Name, query, StringComparison.OrdinalIgnoreCase));
            if (byName == null)
                byName = candidates.FirstOrDefault(x => x.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

            if (byName != null) { name = byName.Name; kind = byName.Kind; return byName.Element; }

            return null;
        }

        /// <summary>
        /// Collect all curves on the given layer from a DWG element's world
        /// geometry (recursively descending into geometry instances).
        /// </summary>
        public static List<Curve> CollectLayerCurves(Document doc, Element target, string layerFilter)
        {
            var collected = new List<Curve>();

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
                        if (string.Equals(LayerName(doc, cv), layerFilter, StringComparison.OrdinalIgnoreCase))
                            collected.Add(cv);
                    }
                }
            }

            var geo = target.get_Geometry(new Options());
            if (geo != null) Walk(geo);
            return collected;
        }

        /// <summary>
        /// Median Z of curve start points (planar DWG assumption); falls back to 0.
        /// </summary>
        public static double MedianZ(List<Curve> curves)
        {
            var zValues = new List<double>();
            foreach (var cv in curves)
            {
                try { zValues.Add(cv.GetEndPoint(0).Z); } catch { }
            }
            if (zValues.Count == 0) return 0;
            zValues.Sort();
            return zValues[zValues.Count / 2];
        }
    }
}
