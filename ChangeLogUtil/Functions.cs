﻿using Microsoft.SqlServer.Server;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ChangeLogUtil
{
    public static class Functions
    {
        [SqlProcedure]
        public static void GetTextTableDefinition(string schema, string name)
        {
            using (var cn = new SqlConnection("context connection=true"))
            {
                var output = GetTextTableDefinition(cn, schema, name);
                SqlContext.Pipe.Send(output);
            }            
        }

        public static string GetTextTableDefinition(SqlConnection cn, string schema, string name)
        {
            using (var cmd = new SqlCommand("SELECT * FROM [changelog].[TableComponents](@schema, @name)", cn))
            using (var adapter = new SqlDataAdapter(cmd))
            {
                cmd.Parameters.AddWithValue("schema", schema);
                cmd.Parameters.AddWithValue("name", name);

                var data = new DataTable();
                adapter.Fill(data);

                var output = new StringBuilder();

                var byType = data.AsEnumerable().ToLookup(row => row.Field<string>("Type"));
                var byParent = data.AsEnumerable().Where(row => !row.IsNull("Parent")).ToLookup(row => row.Field<string>("Parent"));

                AddItems(output, "Columns:", byType["Column"], (xml, children) => ParseColumnDef(xml, children));
                AddItems(output, "Foreign Keys:", byType["ForeignKey"], (xml, children) => ParseForeignKeyDef(xml, children), byParent);
                AddItems(output, "Indexes:", byType["Index"], (xml, children) => ParseIndexDef(xml, children), byParent);
                //AddItems(output, "Check Constraints:", byType["CheckConstraint"], (xml, children) => ParseCheckDef(xml, children));

                return output.ToString();
            }
        }
       
        private static void AddItems(
            StringBuilder output, string heading, 
            IEnumerable<DataRow> componentRows, 
            Func<string, IEnumerable<DataRow>, string> parseDefinition, 
            ILookup<string, DataRow> byParent = null)
        {
            output.AppendLine(heading);

            foreach (var row in componentRows.OrderBy(row => row.Field<int?>("Position")))
            {
                var name = row.Field<string>("Name");
                var childRows = byParent?.Contains(name) ?? false ? byParent[name] : Enumerable.Empty<DataRow>();
                output.AppendLine($"  {name} {parseDefinition(row.Field<string>("Definition"), childRows)}");
            }

            output.AppendLine();
        }

        private static string ParseColumnDef(string xml, IEnumerable<DataRow> childRows)
        {
            var properties = xml.ToDictionary();

            var rules = new Dictionary<Func<string, bool>, IEnumerable<string>>()
            {
                [(type) => type.Contains("var")] = new string[]
                {
                    "length"
                },
                [(type) => type.Contains("char")] = new string[]
                {
                    "length",
                    "collation"
                },
                [(type) => type.Equals("decimal")] = new string[]
                {
                    "precision",
                    "scale"
                },
                [(type) => type.Contains("int")] = new string[]
                {
                    "identity"
                }
            };

            var typeSpecificProps = properties
                .Where(prop_kp => rules.Any(rule_kp => rule_kp.Key.Invoke(prop_kp.Key)))
                .Select(kp => kp.Key)
                .ToArray();

            var allTypeSpecificProps = rules
                .SelectMany(kp => kp.Value)
                .Distinct()
                .ToArray();

            var returnProps = properties
                .Select(kp => kp.Key)
                .Except(allTypeSpecificProps)
                .Concat(typeSpecificProps)
                .ToArray();

            return properties.ToText(returnProps);
        }

        private static string ParseForeignKeyDef(string xml, IEnumerable<DataRow> childRows)
        {
            var properties = xml.ToDictionary();

            var result = $"=> {properties["referencedSchema"]}.{properties["referencedTable"]}:";

            result += string.Join(" + ", childRows.OrderBy(row => row.Field<int?>("Position")).Select(row =>
            {
                var columnProps = row.Field<string>("Definition").ToDictionary();
                return $"{row.Field<string>("Name")} => {columnProps["referencedColumn"]}";
            }));

            return result;
        }

        private static string ParseIndexDef(string xml, IEnumerable<DataRow> childRows)
        {
            var properties = xml.ToDictionary();

            var result = properties.ToText();

            result += string.Join("\r\n", childRows.Select(row =>
            {
                var colProps = row.Field<string>("Definition").ToDictionary();
                return $"{row.Field<string>("Name")} {colProps["sort"]}";
            }));

            return result;
        }

        private static string ParseCheckDef(string xml, IEnumerable<DataRow> childRows)
        {
            throw new NotImplementedException();
        }

        private static Dictionary<string, string> ToDictionary(this string xml) =>
            XDocument.Parse(xml)
            .Descendants()
            .Where(ele => !ele.IsEmpty)
            .ToDictionary(ele => ele.Name.LocalName, ele => ele.Value);

        private static string ToText(this IDictionary<string, string> dictionary, IEnumerable<string> includeKeys = null) =>
            string.Join("\r\n", dictionary
                .Where(kp => includeKeys?.Contains(kp.Key) ?? true)
                .Select(kp => $"    {kp.Key} = {kp.Value}"));
    }
}