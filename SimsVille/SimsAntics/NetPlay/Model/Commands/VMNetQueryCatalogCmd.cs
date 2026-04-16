/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

/*
 * VMNetQueryCatalogCmd (reeims-af0)
 *
 * Query the object catalog. Returns up to 500 entries with {guid, name, price,
 * category, subcategory} as a JSON response frame.
 *
 * Wire format (after [VMCommandType byte]):
 *   [ActorUID: 4 bytes LE]
 *   [category: 7-bit-length-prefixed UTF-8 string]  — "all" or a FunctionFlags name
 *   [hasRequestID: byte]
 *   [if 1: 7-bit-length-prefixed UTF-8 request ID]
 *
 * Response is emitted via VMIPCDriver.SendCatalogResponse and NOT via the generic
 * SendResponseFrame path (RequestID is cleared before returning so that
 * ExecuteIPCCommand does not send a duplicate frame).
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FSO.Content;
using FSO.SimAntics.NetPlay.Drivers;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetQueryCatalogCmd : VMNetCommandBodyAbstract
    {
        /// <summary>
        /// Category filter. "all" (or empty) returns everything.
        /// Otherwise matches against the FunctionFlags-derived category name.
        /// </summary>
        public string Category = "all";

        // Maximum entries per response.
        private const int MaxEntries = 500;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            // Capture RequestID before clearing it — we send our own response frame.
            var reqId = RequestID;
            RequestID = null; // prevents ExecuteIPCCommand from sending a duplicate frame

            var driver = vm.Driver as VMIPCDriver;
            if (driver == null)
                return false; // not running under IPC driver

            var contentProvider = FSO.Content.Content.Get();
            if (contentProvider?.WorldObjects?.Entries == null)
            {
                // Catalog not yet loaded — acceptable; agent may poll.
                if (reqId != null)
                    driver.SendCatalogResponse(reqId, "[]");
                return true;
            }

            var entries = contentProvider.WorldObjects.Entries;
            var catalog = new List<CatalogEntry>(Math.Min(entries.Count, MaxEntries));

            var filterAll = string.IsNullOrEmpty(Category) || Category == "all";
            var filterLower = filterAll ? null : Category.ToLowerInvariant();

            var count = 0;
            foreach (var kvp in entries)
            {
                if (count >= MaxEntries)
                    break;

                var objRef = kvp.Value;

                // Try to get OBJD metadata from cache (no forced IFF load).
                uint price = 0;
                string categoryName = "misc";
                string subcategoryName = "none";

                var obj = contentProvider.WorldObjects.GetCached((uint)objRef.ID);
                if (obj?.OBJ != null)
                {
                    price = obj.OBJ.Price;
                    categoryName = FunctionFlagsToCategory(obj.OBJ.FunctionFlags);
                    subcategoryName = FunctionFlagsToSubcategory(obj.OBJ.FunctionFlags);
                }

                if (!filterAll && categoryName != filterLower)
                    continue;

                var name = objRef.Name ?? string.Empty;
                catalog.Add(new CatalogEntry
                {
                    guid = (uint)objRef.ID,
                    name = name,
                    price = price,
                    category = categoryName,
                    subcategory = subcategoryName
                });
                count++;
            }

            var json = BuildCatalogJson(catalog);

            if (reqId != null)
                driver.SendCatalogResponse(reqId, json);

            return true;
        }

        #region Serialization

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer); // writes ActorUID
            writer.Write(Category ?? "all");
            SerializeRequestID(writer);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader); // reads ActorUID + sets FromNet
            Category = reader.ReadString();
            DeserializeRequestID(reader);
        }

        #endregion

        // --- static helpers ---

        /// <summary>Maps FunctionFlags bits to a buy-mode category name.</summary>
        internal static string FunctionFlagsToCategory(ushort flags)
        {
            if (flags == 0) return "misc";
            // Sims 1 FunctionFlags — low byte is category, high byte is sub-category.
            // Low byte values (from Maxis SDK / community docs):
            switch (flags & 0xFF)
            {
                case 0x01: return "seating";
                case 0x02: return "surfaces";
                case 0x04: return "appliances";
                case 0x08: return "electronics";
                case 0x10: return "plumbing";
                case 0x20: return "decorative";
                case 0x40: return "misc";
                case 0x80: return "lighting";
                default:   return "misc";
            }
        }

        /// <summary>Maps FunctionFlags high byte to a subcategory name.</summary>
        internal static string FunctionFlagsToSubcategory(ushort flags)
        {
            switch ((flags >> 8) & 0xFF)
            {
                case 0x01: return "indoor";
                case 0x02: return "outdoor";
                case 0x04: return "kitchen";
                case 0x08: return "bathroom";
                case 0x10: return "bedroom";
                case 0x20: return "livingroom";
                case 0x40: return "dining";
                default:   return "none";
            }
        }

        /// <summary>Builds the catalog JSON array string without a JSON library.</summary>
        private static string BuildCatalogJson(List<CatalogEntry> catalog)
        {
            var sb = new StringBuilder(catalog.Count * 80);
            sb.Append('[');
            for (int i = 0; i < catalog.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = catalog[i];
                sb.Append("{\"guid\":");
                sb.Append(e.guid);
                sb.Append(",\"name\":\"");
                sb.Append(JsonEscape(e.name));
                sb.Append("\",\"price\":");
                sb.Append(e.price);
                sb.Append(",\"category\":\"");
                sb.Append(e.category);
                sb.Append("\",\"subcategory\":\"");
                sb.Append(e.subcategory);
                sb.Append("\"}");
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            if (s == null) return string.Empty;
            // Replace backslash and double-quote for safe JSON embedding.
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private struct CatalogEntry
        {
            public uint guid;
            public string name;
            public uint price;
            public string category;
            public string subcategory;
        }
    }
}
