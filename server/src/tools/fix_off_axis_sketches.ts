import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerFixOffAxisSketchesTool(server: McpServer) {
  server.tool(
    "fix_off_axis_sketches",
    "Fix slightly off-axis sketch profiles in Floors, Ceilings, and FootPrintRoofs using exact ray-ray intersection reconstruction for 100% closed boundary loops.",
    {
      hostIds: z
        .union([z.array(z.number()), z.string()])
        .optional()
        .describe("List or CSV of host element IDs (Floor, Ceiling, Roof) to process."),
      lineIds: z
        .union([z.array(z.number()), z.string()])
        .optional()
        .describe("Optional list or CSV of specific flagged sketch line IDs to snap."),
      minAngleDeg: z
        .number()
        .default(0.0000001)
        .optional()
        .describe("Minimum angular deviation in degrees (default 0.0000001)"),
      maxAngleDeg: z
        .number()
        .default(0.1)
        .optional()
        .describe("Maximum angular deviation in degrees (default 0.1)"),
    },
    async (args, _extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("fix_off_axis_sketches", {
            hostIds: args.hostIds,
            lineIds: args.lineIds,
            minAngleDeg: args.minAngleDeg,
            maxAngleDeg: args.maxAngleDeg,
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
              text: `Fix off-axis sketches failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
