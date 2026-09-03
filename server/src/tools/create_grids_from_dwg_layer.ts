import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCreateGridsFromDwgLayerTool(server: McpServer) {
  server.tool(
    "create_grids_from_dwg_layer",
    "Generate Revit grids from long straight lines on one DWG layer. Lines are ordered by angle bucket (15 degrees) then position and labeled with a single alphabetic (A,B,C...) or numeric (1,2,3...) sequence - rename afterward as needed. Circles/short ticks are skipped. Level defaults to the one nearest the layer's median elevation.",
    {
      dwgNameOrId: z
        .string()
        .describe("Element ID or name (exact or partial) of the imported/linked DWG."),
      layer: z
        .string()
        .describe("Exact DWG layer name containing the grid lines (e.g. 'A-GRID'). Use extract_dwg_curves to list layers."),
      minLengthFt: z
        .number()
        .positive()
        .default(5)
        .optional()
        .describe("Minimum line length in feet to qualify as a grid datum (default 5). Shorter lines are skipped."),
      namingStyle: z
        .enum(["alphabetic", "numeric"])
        .default("alphabetic")
        .optional()
        .describe("Label sequence style (default alphabetic). Duplicates of existing grid names are auto-suffixed."),
      startLabel: z
        .string()
        .optional()
        .describe("Starting label (default 'A' or '1')."),
      levelId: z
        .number()
        .int()
        .optional()
        .describe("Optional Revit level ElementId to host the grids. If omitted, the level nearest the layer's median Z is used."),
    },
    async (args, _extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_grids_from_dwg_layer", {
            dwgNameOrId: args.dwgNameOrId,
            layer: args.layer,
            minLengthFt: args.minLengthFt ?? 5,
            namingStyle: args.namingStyle ?? "alphabetic",
            ...(args.startLabel ? { startLabel: args.startLabel } : {}),
            ...(args.levelId ? { levelId: args.levelId } : {}),
          });
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
              text: `Create grids from DWG layer failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
