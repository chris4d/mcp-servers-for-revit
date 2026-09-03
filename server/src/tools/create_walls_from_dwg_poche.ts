import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCreateWallsFromDwgPocheTool(server: McpServer) {
  server.tool(
    "create_walls_from_dwg_poche",
    "Generate Revit walls from the hatch fills of an imported or linked DWG (poche regions). Works on the programmatic representation of graphical hatches: DWG hatch entities come into Revit as flat (volume-zero) solids with no solid-level layer; the source DWG layer lives on Face.GraphicsStyleId of each face. This tool scans flat solids, keeps faces on the requested DWG layer (or all of them if no layer is given), extracts each face's boundary loop, merges collinear edges into straight runs, pairs runs as the two faces of one wall (exact corners), and merges collinear centerline pieces across loops. Regions wider than the thickness band (2in..maxWallThicknessFt) are ignored automatically (room fills etc). Thickness maps to the nearest Revit wall type by compound width. Short centerlines are rejected as door-jamb linework; door-swing arcs optionally reject short centers. Use create_walls_from_dwg_layer instead when you want to work with lines and polylines.",
    {
      dwgNameOrId: z
        .string()
        .describe("Element ID or name (exact or partial) of the imported/linked DWG."),
      pocheLayer: z
        .string()
        .optional()
        .describe("Optional DWG layer to restrict which hatch-fill faces count as poche (matched on Face.GraphicsStyleId, case-insensitive). Omit to accept hatch fills from every layer."),
      heightFt: z
        .number()
        .positive()
        .default(10)
        .optional()
        .describe("Wall height in feet above the level (default 10)."),
      maxWallThicknessFt: z
        .number()
        .positive()
        .default(3.0)
        .optional()
        .describe("Maximum pairing distance in feet (default 3.0 = 36in). Minimum is fixed at 2in."),
      minWallLengthFt: z
        .number()
        .positive()
        .default(3.5)
        .optional()
        .describe("Minimum wall centerline length in feet; shorter centerlines are rejected as jamb linework (default 3.5)."),
      wallTypeName: z
        .string()
        .optional()
        .describe("Optional wall type name (exact or partial) to force for all walls. If omitted, the nearest type by thickness is chosen per wall."),
      levelId: z
        .number()
        .int()
        .optional()
        .describe("Optional Revit level ElementId. If omitted, the level nearest the hatch-fill vertices' median Z is used."),
      excludeDoorArcs: z
        .boolean()
        .default(true)
        .optional()
        .describe("Reject short wall centerlines spanning detected door-swing arcs (default true)."),
      maxWalls: z
        .number()
        .int()
        .positive()
        .default(200)
        .optional()
        .describe("Maximum walls to create (default 200, hard cap 5000)."),
    },
    async (args, _extra) => {
      const params: Record<string, unknown> = {
        dwgNameOrId: args.dwgNameOrId,
        pocheLayer: args.pocheLayer ?? "",
        heightFt: args.heightFt ?? 10,
        maxWallThicknessFt: args.maxWallThicknessFt ?? 3.0,
        minWallLengthFt: args.minWallLengthFt ?? 3.5,
        excludeDoorArcs: args.excludeDoorArcs ?? true,
        maxWalls: args.maxWalls ?? 200,
      };
      if (args.wallTypeName) params["wallTypeName"] = args.wallTypeName;
      if (args.levelId) params["levelId"] = args.levelId;

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_walls_from_dwg_poche", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response, null, 2),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Create walls from DWG poche failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
