using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RevitMCPCommandSet.Models.Common
{
    /// <summary>
    /// Filter settings - supports combined condition filtering
    /// </summary>
    public class FilterSetting
    {
        /// <summary>
        /// Gets or sets the Revit built-in category name to filter by (e.g. "OST_Walls").
        /// If null or empty, no category filtering is applied.
        /// </summary>
        [JsonProperty("filterCategory")]
        public string FilterCategory { get; set; } = null;
        /// <summary>
        /// Gets or sets the Revit element type name to filter by (e.g. "Wall" or "Autodesk.Revit.DB.Wall").
        /// If null or empty, no type filtering is applied.
        /// </summary>
        [JsonProperty("filterElementType")]
        public string FilterElementType { get; set; } = null;
        /// <summary>
        /// Gets or sets the ElementId value of the family type (FamilySymbol) to filter by.
        /// If 0 or negative, no family filtering is applied.
        /// Note: this filter applies only to element instances, not to type elements.
        /// </summary>
        [JsonProperty("filterFamilySymbolId")]
        public int FilterFamilySymbolId { get; set; } = -1;
        /// <summary>
        /// Gets or sets whether to include element types (e.g. wall types, door types, etc.)
        /// </summary>
        [JsonProperty("includeTypes")]
        public bool IncludeTypes { get; set; } = false;
        /// <summary>
        /// Gets or sets whether to include element instances (e.g. placed walls, doors, etc.)
        /// </summary>
        [JsonProperty("includeInstances")]
        public bool IncludeInstances { get; set; } = true;
        /// <summary>
        /// Gets or sets whether to return only elements visible in the current view.
        /// Note: this filter applies only to element instances, not to type elements.
        /// </summary>
        [JsonProperty("filterVisibleInCurrentView")]
        public bool FilterVisibleInCurrentView { get; set; }
        /// <summary>
        /// Gets or sets the minimum point coordinates for spatial filtering (unit: mm)
        /// If set along with BoundingBoxMax, elements intersecting this bounding box will be selected
        /// </summary>
        [JsonProperty("boundingBoxMin")]
        public JZPoint BoundingBoxMin { get; set; } = null;
        /// <summary>
        /// Gets or sets the maximum point coordinates for spatial filtering (unit: mm)
        /// If set along with BoundingBoxMin, elements intersecting this bounding box will be selected
        /// </summary>
        [JsonProperty("boundingBoxMax")]
        public JZPoint BoundingBoxMax { get; set; } = null;
        /// <summary>
        /// Maximum element count limit
        /// </summary>
        [JsonProperty("maxElements")]
        public int MaxElements { get; set; } = 50; 
        /// <summary>
        /// Validates the filter settings and checks for potential conflicts
        /// </summary>
        /// <returns>True if the settings are valid, otherwise false</returns>
        public bool Validate(out string errorMessage)
        {
            errorMessage = null;

            // Check whether at least one kind of element is selected
            if (!IncludeTypes && !IncludeInstances)
            {
                errorMessage = "过滤设置无效: 必须至少包含元素类型或元素实例之一";
                return false;
            }

            // Check whether at least one filter condition is specified
            if (string.IsNullOrWhiteSpace(FilterCategory) &&
                string.IsNullOrWhiteSpace(FilterElementType) &&
                FilterFamilySymbolId <= 0)
            {
                errorMessage = "过滤设置无效: 必须至少指定一个过滤条件(类别、元素类型或族类型)";
                return false;
            }

            // Check for conflicts between type elements and certain filters
            if (IncludeTypes && !IncludeInstances)
            {
                List<string> invalidFilters = new List<string>();
                if (FilterFamilySymbolId > 0)
                    invalidFilters.Add("族实例过滤");
                if (FilterVisibleInCurrentView)
                    invalidFilters.Add("视图可见性过滤");
                if (invalidFilters.Count > 0)
                {
                    errorMessage = $"当仅过滤类型元素时，以下过滤器不适用: {string.Join(", ", invalidFilters)}";
                    return false;
                }
            }
            // Check the validity of the spatial range filter
            if (BoundingBoxMin != null && BoundingBoxMax != null)
            {
                // Ensure the minimum point is less than or equal to the maximum point
                if (BoundingBoxMin.X > BoundingBoxMax.X ||
                    BoundingBoxMin.Y > BoundingBoxMax.Y ||
                    BoundingBoxMin.Z > BoundingBoxMax.Z)
                {
                    errorMessage = "空间范围过滤器设置无效: 最小点坐标必须小于或等于最大点坐标";
                    return false;
                }
            }
            else if (BoundingBoxMin != null || BoundingBoxMax != null)
            {
                errorMessage = "空间范围过滤器设置无效: 必须同时设置最小点和最大点坐标";
                return false;
            }
            return true;
        }
    }
}
