using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Data.SqlClient;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace FindyBot3000.AzureFunction
{
    public class Command
    {
        public const string FindItem = "FindItem";
        public const string FindTags = "FindTags";
        public const string InsertItem = "InsertItem";
        public const string RemoveItem = "RemoveItem";
        public const string AddTags = "AddTags";
        public const string UpdateQuantity = "UpdateQuantity";
        public const string SetQuantity = "SetQuantity";
    }

    // Sql Table column names
    public class Dbo
    {
        public class Items
        {
            public const string Name = "Name";
            public const string Quantity = "Quantity";
            public const string Row = "Row";
            public const string Col = "Col";
            public const string SmallBox = "SmallBox";
            public const string DateCreated = "DateCreated";
            public const string LastUpdated = "LastUpdated";
        }

        public class Tags
        {
            public const string Name = "Name";
            public const string Tag = "Tag";
        }

        public class Commands
        {
            public const string DateCreated = "DateCreated";
            public const string Command = "Command";
            public const string DataIn = "DataIn";
            public const string DataOut = "DataOut";
        }

        public class HttpRequests
        {
            public const string HttpRequestBody = "HttpRequestBody";
            public const string DateCreated = "DateCreated";
        }
    }

    public class FindTagsResponse
    {
        public string Name { get; set; }
        public int Quantity { get; set; }
        public int Row { get; set; }
        public int Col { get; set; }
        public int TagsMatched { get; set; }
        public float Confidence { get; set; }
    }

    // This begs for a stateful azure function...
    public class MatrixModel
    {
        private const int TopRows = 8;
        private const int TopCols = 16;
        private const int BottomRows = 6;
        private const int BottomCols = 8;

        private bool[,] TopItems = new bool[TopRows, TopCols];
        private bool[,] BottomItems = new bool[BottomRows, BottomCols];

        public void AddItem(int row, int col)
        {
            if (row < 8)
            {
                this.TopItems[row, col] = true;
            }
            else if (row < 14)
            {
                this.BottomItems[row, col] = true;
            }
        }

        public (int, int) GetNextAvailableBox(bool isSmallBox)
        {
            if (isSmallBox)
            {
                return this.GetBoxAndUpdate(TopItems, TopRows, TopCols);
            }
            else
            {
                (int row, int col) = this.GetBoxAndUpdate(BottomItems, BottomRows, BottomCols);

                // 8 rows of small boxes on top, with 6 rows of big boxes below.
                // Indexing for rows and columns start at top left.
                row += 8;

                return (row, col);
            }
        }

        private (int, int) GetBoxAndUpdate(bool[,] matrix, int rows, int cols)
        {
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    if (matrix[row, col] == false)
                    {
                        matrix[row, col] = true;
                        return (row, col);
                    }
                }
            }
            return (-1, -1);
        }
    }

    public static class SqlCommandAzureFunction
    {
        [FunctionName("SqlCommandAzureFunction")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log,
            ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            string sqldb_connection = config.GetConnectionString("sqldb_connection");

            log.LogInformation(sqldb_connection);
            string response = string.Empty;

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            log.LogInformation(requestBody);

            using (SqlConnection connection = new SqlConnection(sqldb_connection))
            {
                connection.Open();

                LogHttpRequestBody(connection, requestBody);

                dynamic eventData = JsonConvert.DeserializeObject(requestBody);

                if (eventData == null)
                {
                    return new BadRequestObjectResult(
                        "Could not parse JSON input");
                }

                string unescapedJson = ((string)eventData.data).Replace(@"\", "");
                dynamic jsonRequest = JsonConvert.DeserializeObject(unescapedJson);

                if (jsonRequest == null || jsonRequest.command == null || jsonRequest.data == null)
                {
                    return new BadRequestObjectResult(
                        "Could not parse command JSON in data tag");
                }

                string command = jsonRequest.command;
                dynamic data = jsonRequest.data;

                switch (command)
                {
                    case Command.FindItem:
                        response = FindItem(data, connection, log);
                        break;

                    case Command.FindTags:
                        response = FindTags(data, connection, log);
                        break;

                    case Command.InsertItem:
                        response = InsertItem(data, connection, log);
                        break;

                    case Command.RemoveItem:
                        response = RemoveItem(data, connection, log);
                        break;

                    case Command.AddTags:
                        response = AddTags(data, connection, log);
                        break;

                    case Command.UpdateQuantity:
                        response = UpdateQuantity(data, connection, log);
                        break;

                    case Command.SetQuantity:
                        response = SetQuantity(data, connection, log);
                        break;

                    default:
                        response = $"Command '{command}' not supported";
                        break;
                }

                try
                {
                    var requestResponseLogString = string.Format($"INSERT INTO dbo.Commands ([DateCreated], [Command], [DataIn], [DataOut]) VALUES (@param1, @param2, @param3, @param4)");
                    using (SqlCommand sqlCommand = new SqlCommand())
                    {
                        sqlCommand.Connection = connection;
                        sqlCommand.CommandText = requestResponseLogString;
                        sqlCommand.Parameters.AddWithValue("@param1", DateTime.Now);
                        sqlCommand.Parameters.AddWithValue("@param2", command);
                        sqlCommand.Parameters.AddWithValue("@param3", Convert.ToString(data));
                        sqlCommand.Parameters.AddWithValue("@param4", response);
                        sqlCommand.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    log.LogInformation(ex.Message);
                }
            }

            return new OkObjectResult(response); // response
        }

        public static string FindItem(dynamic jsonRequestData, SqlConnection connection, ILogger log)
        {
            string item = jsonRequestData;
            var queryString = string.Format($"SELECT * FROM dbo.Items WHERE LOWER(Items.Name) LIKE '{item.ToLowerInvariant()}'");

            using (SqlCommand command = new SqlCommand(queryString, connection))
            {
                SqlDataReader reader = command.ExecuteReader();
                try
                {
                    List<object> jsonObjects = new List<object>();
                    while (reader.Read())
                    {
                        jsonObjects.Add(
                            new
                            {
                                Name = (string)reader[Dbo.Items.Name],
                                Quantity = (int)reader[Dbo.Items.Quantity],
                                Row = (int)reader[Dbo.Items.Row],
                                Col = (int)reader[Dbo.Items.Col]
                            });
                    }

                    var response = new
                    {
                        Command = Command.FindItem,
                        Count = jsonObjects.Count,
                        Result = jsonObjects
                    };

                    string jsonQueryResponse = JsonConvert.SerializeObject(response);
                    log.LogInformation(jsonQueryResponse);

                    return jsonQueryResponse;
                }
                finally
                {
                    // Always call Close when done reading.
                    reader.Close();
                }
            }
        }

        public static string FindTags(dynamic jsonRequestData, SqlConnection connection, ILogger log)
        {
            string words = jsonRequestData;
            // Take a string of words: "Green motor driver"
            // Split it into an array of strings: string[] = { "Green", "motor", "driver" }
            // Format the words to be suited for the SQL-query: "'green','motor','driver'"
            string[] tags = words.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string formattedTags = string.Join(",", tags.Select(tag => string.Format("'{0}'", tag.Trim().ToLowerInvariant())));
            log.LogInformation(formattedTags);

            var queryString = $@"
SELECT i.Name, i.Quantity, i.Row, i.Col, t.TagsMatched
FROM dbo.Items i JOIN
(
    SELECT Name, COUNT(Name) TagsMatched
    FROM dbo.Tags
    WHERE Tag IN({formattedTags})
    GROUP BY Name
) t ON i.Name = t.Name
ORDER BY t.TagsMatched DESC";

            using (SqlCommand command = new SqlCommand(queryString, connection))
            {
                SqlDataReader reader = command.ExecuteReader();
                try
                {
                    bool compact = true;

                    List<object> jsonObjects = new List<object>();
                    while (reader.Read())
                    {
                        int tagsMatched = (int)reader["TagsMatched"];
                        float confidence = (float)tagsMatched / tags.Length;

                        if (compact)
                        {
                            jsonObjects.Add(new List<object>() { (int)reader["Row"], (int)reader["Col"], (float)confidence });
                        }
                        else
                        {
                            jsonObjects.Add(
                                new
                                {
                                    Name = (string)reader["Name"],
                                //Quantity = (int)reader["Quantity"],
                                Info = new List<object>() { (int)reader["Row"], (int)reader["Col"], (float)confidence },
                                //TagsMatched = tagsMatched,
                                //Confidence = confidence
                            });
                        }
                    }

                    var response = new
                    {
                        Command = Command.FindTags,
                        Count = jsonObjects.Count,
                        Result = jsonObjects
                    };

                    string jsonQueryResponse = JsonConvert.SerializeObject(response, new FloatFormatConverter());
                    log.LogInformation(jsonQueryResponse);

                    return jsonQueryResponse;
                }
                finally
                {
                    // Always call Close when done reading.
                    reader.Close();
                }
            }
        }

        // 1. Check if item exists, if yes, return the box it's in.
        // 2. Query for currently used boxes, find an empty box if one exists, and return it's row/column location
        // 3. Insert an entry into the Items table with the row/column info
        // 4. If successful, insert entries into the Tags table with the words from the item name as tags
        // 4. Return a response with data indicating the insert was successful, and the row/column info
        //
        // Format of info can be:
        // a. "<item name>"
        // b. "<item name> into a <small box|big box> with tags <tag0 tag1 tag2 ...>"
        // c. "<item name> with tags <tag0 tag1 tag2 ...> into a <small box|big box>"
        public static string InsertItem(dynamic jsonRequestData, SqlConnection connection, ILogger log)
        {
            string info = jsonRequestData["Info"];
            int quantity = jsonRequestData["Quantity"];

            string infoLower = info.ToLowerInvariant();

            bool hasBox = TryGetBoxInfo(infoLower, out int boxIndex, out string boxSearch, out bool useSmallBox);
            bool hasTags = TryGetTagsInfo(infoLower, boxIndex, out int tagsIndex, out HashSet<string> tags);
            string item = GetItemInfo(info, hasBox, hasTags, boxIndex, tagsIndex);

            string itemLower = item.ToLowerInvariant();

            tags.UnionWith(itemLower.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()));

            var checkIfExistsQuery = $@"SELECT Name,Quantity,Row,Col FROM dbo.Items WHERE LOWER(Items.Name) LIKE '{itemLower}'";

            using (SqlCommand command = new SqlCommand(checkIfExistsQuery, connection))
            {
                SqlDataReader reader = command.ExecuteReader();

                try
                {
                    if (reader.HasRows)
                    {
                        // There will only be one object in this list
                        List<object> jsonObjects = new List<object>();
                        while (reader.Read())
                        {
                            jsonObjects.Add(
                                new
                                {
                                    Name = (string)reader["Name"],
                                    Quantity = (int)reader["Quantity"],
                                    Row = (int)reader["Row"],
                                    Col = (int)reader["Col"]
                                });
                        }

                        var response = new
                        {
                            Command = Command.FindItem,
                            Count = jsonObjects.Count,
                            Result = jsonObjects
                        };

                        string jsonQueryResponse = JsonConvert.SerializeObject(response);
                        log.LogInformation(jsonQueryResponse);

                        return jsonQueryResponse;
                    }
                }
                catch (Exception ex)
                {
                    log.LogInformation(ex.Message);
                }
                finally
                {
                    // Always call Close when done reading.
                    reader.Close();
                }
            }            

            // Item doesn't exist; insert.
            // Find existing boxes
            var sqlAllConsumedBoxes = string.Format("SELECT ROW, COL FROM dbo.Items");
            MatrixModel matrix = new MatrixModel();

            using (SqlCommand command = new SqlCommand(sqlAllConsumedBoxes, connection))
            {
                SqlDataReader reader = command.ExecuteReader();

                try
                {
                    if (reader.HasRows)
                    {
                        while (reader.Read())
                        {
                            matrix.AddItem((int)reader[Dbo.Items.Row], (int)reader[Dbo.Items.Col]);
                        }
                    }
                }
                finally
                {
                    reader.Close();
                }
            }

            var (row, col) = matrix.GetNextAvailableBox(useSmallBox);

            if (row == -1 && col == -1)
            {
                return JsonConvert.SerializeObject(
                    new
                    {
                        Command = Command.InsertItem,
                        InsertSucceeded = false,
                        Message = $"No {(useSmallBox ? "Small" : "Large")} boxes left!"
                    });
            }

            var sqlInsertString = string.Format($@"
INSERT INTO dbo.Items([Name], [Quantity], [Row], [Col], [IsSmallBox], [DateCreated], [LastUpdated])
VALUES (@param1, @param2, @param3, @param4, @param5, @param6, @param7)");

            bool insertSucceeded = false;
            using (SqlCommand sqlCommand = new SqlCommand())
            {
                sqlCommand.Connection = connection;
                sqlCommand.CommandText = sqlInsertString;
                sqlCommand.Parameters.AddWithValue("@param1", item);
                sqlCommand.Parameters.AddWithValue("@param2", quantity);
                sqlCommand.Parameters.AddWithValue("@param3", row);
                sqlCommand.Parameters.AddWithValue("@param4", col);
                sqlCommand.Parameters.AddWithValue("@param5", useSmallBox);
                sqlCommand.Parameters.AddWithValue("@param6", DateTime.UtcNow);
                sqlCommand.Parameters.AddWithValue("@param7", DateTime.UtcNow);
                insertSucceeded = sqlCommand.ExecuteNonQuery() > 0;
            }

            if (!insertSucceeded)
            {
                return JsonConvert.SerializeObject(
                    new
                    {
                        Command = Command.InsertItem,
                        InsertSucceeded = false,
                        Message = "Insert failed"
                    });
            }

            // Todo: Revert adding to dbo.Items if inserting to dbo.Items fails
            int tagsAdded = InsertTags(connection, item, tags);

            object insertResponse = new
            {
                Command = Command.InsertItem,
                InsertSucceeded = insertSucceeded && tagsAdded > 0,
                Row = row,
                Col = col
            };

            return JsonConvert.SerializeObject(insertResponse);
        }
        
        public static string RemoveItem(dynamic jsonRequestData, SqlConnection connection, ILogger log)
        {
            string itemLower = ((string)jsonRequestData).ToLowerInvariant();
            var queryString = $@"
DELETE FROM dbo.Tags  WHERE LOWER(Tags.Name)  LIKE '{itemLower}';
DELETE FROM dbo.Items WHERE LOWER(Items.Name) LIKE '{itemLower}';";

            using (SqlCommand command = new SqlCommand(queryString, connection))
            {
                int itemsRemoved = command.ExecuteNonQuery();

                object removeItemResponse = new
                {
                    Command = Command.RemoveItem,
                    Success = itemsRemoved > 0,
                    Quantity = itemsRemoved
                };

                return JsonConvert.SerializeObject(removeItemResponse);
            }
        }

        // 1. Verify item exists
        // 2. Add tags
        public static string AddTags(dynamic jsonRequestData, SqlConnection connection, ILogger log)
        {
            string item = jsonRequestData["Item"];
            string itemLower = item.ToLowerInvariant();
            string itemExistsQuery = $@"
SELECT CASE WHEN EXISTS (
    SELECT *
    FROM dbo.Items
    WHERE LOWER(Items.Name) LIKE '{itemLower}'
)
THEN CAST(1 AS BIT)
ELSE CAST(0 AS BIT) END";

            using (SqlCommand command = new SqlCommand(itemExistsQuery, connection))
            {
                SqlDataReader reader = command.ExecuteReader();
                try
                {
                    if (!reader.HasRows)
                    {
                        var addTagsResponse = new
                        {
                            Command = Command.FindItem,
                            Success = false,
                            Message = "Item does not exist, cannot add tags"
                        };

                        string jsonQueryResponse = JsonConvert.SerializeObject(addTagsResponse);
                        log.LogInformation(jsonQueryResponse);

                        return jsonQueryResponse;
                    }
                }
                finally
                {
                    // Always call Close when done reading.
                    reader.Close();
                }
            }

            string tagString = jsonRequestData["Tags"];
            IEnumerable<string> tags = tagString.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim());

            int tagsAdded = InsertTags(connection, item, tags);

            object addTagsResponse2 = new
            {
                Command = Command.AddTags,
                Success = tagsAdded > 0,
                Count = tagsAdded
            };

            return JsonConvert.SerializeObject(addTagsResponse2);
        }

        public static string SetQuantity(dynamic jsonRequestData, SqlConnection connection, ILogger log)
        {
            string item = jsonRequestData["Item"];
            int quantity = jsonRequestData["Quantity"];

            var setQuantityQuery = $@"
UPDATE dbo.Items
SET Items.Quantity = {quantity}
WHERE LOWER(Items.Name) LIKE '{item.ToLowerInvariant()}'";

            int itemsUpdated = 0;
            using (SqlCommand command = new SqlCommand(setQuantityQuery, connection))
            {
                itemsUpdated = command.ExecuteNonQuery();
            }

            // Return the item, so the display lights the box
            return FindItem(item, connection, log);
        }

        public static string UpdateQuantity(dynamic jsonRequestData, SqlConnection connection, ILogger log)
        {
            string item = jsonRequestData["Item"];
            int quantity = jsonRequestData["Quantity"];
            bool adding = jsonRequestData["Add"];

            if (!adding)
            {
                quantity *= -1;
            }

            var updateQuantityQuery = $@"
UPDATE dbo.Items
SET Items.Quantity = Items.Quantity + {quantity}
WHERE LOWER(Items.Name) LIKE '{item.ToLowerInvariant()}'";

            int itemsUpdated = 0;
            using (SqlCommand command = new SqlCommand(updateQuantityQuery, connection))
            {
                itemsUpdated = command.ExecuteNonQuery();
            }

            // Return the item, so the display lights the box
            return FindItem(item, connection, log);
        }

        /* Build a SQL insert statement supporting multiple insert values, without duplicating any entries:
             MERGE INTO dbo.Tags AS Target
             USING(VALUES (@param1, @param2),(@param1, @param3)) AS Source (Name, Tag)
             ON Target.Name = Source.Name AND Target.Tag = Source.Tag
             WHEN NOT MATCHED BY Target THEN
             INSERT(Name, Tag) VALUES(Source.Name, Source.Tag);

            After substitution:
            USING(VALUES ('AA Battery', 'aa'),('AA Battery', 'battery')) AS Source (Name, Tag)
        */
        private static int InsertTags(SqlConnection connection, string item, IEnumerable<string> tags)
        {
            string insertTagsCommand = $@"
MERGE INTO dbo.Tags AS Target
USING(VALUES {string.Join(",", tags.Select((_, index) => $"(@param1, @param{index + 2})"))}) AS Source (Name, Tag)
ON Target.Name = Source.Name AND Target.Tag = Source.Tag
WHEN NOT MATCHED BY Target THEN
INSERT(Name, Tag) VALUES(Source.Name, Source.Tag);";

            int tagsAdded = 0;
            using (SqlCommand sqlCommand = new SqlCommand())
            {
                sqlCommand.Connection = connection;
                sqlCommand.CommandText = insertTagsCommand;
                sqlCommand.Parameters.AddWithValue("@param1", item);
                int i = 2;
                foreach (string tag in tags)
                {
                    sqlCommand.Parameters.AddWithValue($"@param{i++}", tag);
                }
                tagsAdded = sqlCommand.ExecuteNonQuery();
            }

            return tagsAdded;
        }

        public static void LogHttpRequestBody(SqlConnection connection, string requestBody)
        {
            var httpRequestString = $"INSERT INTO dbo.HttpRequests ([HttpRequestBody], [DateCreated]) VALUES (@param1, @param2)";
            using (SqlCommand sqlCommand = new SqlCommand())
            {
                sqlCommand.Connection = connection;
                sqlCommand.CommandText = httpRequestString;
                sqlCommand.Parameters.AddWithValue("@param1", requestBody);
                sqlCommand.Parameters.AddWithValue("@param2", DateTime.Now);
                sqlCommand.ExecuteNonQuery();
            }
        }

        // InsertItem helper methods

        public class BoxType
        {
            public string Type { get; set; }
            public bool IsSmallBox { get; set; }

            public BoxType(string type, bool isSmallBox)
            {
                this.Type = type;
                this.IsSmallBox = isSmallBox;
            }
        }

        public static bool TryGetBoxInfo(string info, out int startIndex, out string searchTerm, out bool useSmallBox)
        {
            string[] boxPrefix = { "into a", "in a" };
            BoxType[] boxTypes =
            {
                new BoxType("big", false), new BoxType("large", false),
                new BoxType("small", true), new BoxType("little", true)
            };
            string[] boxNames = { "box", "container" };
            foreach (string prefix in boxPrefix)
            {
                foreach (BoxType boxType in boxTypes)
                {
                    foreach (string name in boxNames)
                    {
                        string searchString = $" {prefix} {boxType.Type} {name}";
                        int index = info.IndexOf(searchString);
                        if (index != -1)
                        {
                            useSmallBox = boxType.IsSmallBox;
                            searchTerm = searchString;
                            startIndex = index;
                            return true;
                        }
                    }
                }
            }

            searchTerm = string.Empty;
            useSmallBox = true;
            startIndex = -1;
            return false;
        }

        public static bool TryGetTagsInfo(string info, int boxIndex, out int startIndex, out HashSet<string> tags)
        {
            string tagsTag = " with tags ";
            startIndex = info.IndexOf(tagsTag);

            if (startIndex == -1)
            {
                tags = new HashSet<string>();
                return false;
            }

            string tagsString = string.Empty;
            int tagsStartIndex = startIndex + tagsTag.Length;

            if (startIndex < boxIndex)
            {
                tagsString = info.Substring(tagsStartIndex, boxIndex - tagsStartIndex);
            }
            else
            {
                tagsString = info.Substring(tagsStartIndex);
            }

            if (!string.IsNullOrEmpty(tagsString))
            {
                tagsString = tagsString.ToLowerInvariant();

                tags = new HashSet<string>(
                    tagsString
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(a => a.Trim()));

                return true;
            }
            else
            {
                tags = new HashSet<string>();
                return false;
            }
        }

        public static string GetItemInfo(string info, bool hasBox, bool hasTags, int boxIndex, int tagsIndex)
        {
            string item = string.Empty;
            if (hasBox && hasTags)
            {
                item = info.Substring(0, tagsIndex < boxIndex ? tagsIndex : boxIndex);
                return item;
            }
            else if (hasBox)
            {
                item = info.Substring(0, boxIndex);
                return item;
            }
            else if (hasTags)
            {
                item = info.Substring(0, tagsIndex);
                return item;
            }
            return info;
        }
    }

    public class FloatFormatConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return (objectType == typeof(float));
        }

        public override void WriteJson(JsonWriter writer, object value,
                                       JsonSerializer serializer)
        {
            writer.WriteRawValue(string.Format("{0:N2}", value));
        }

        public override bool CanRead
        {
            get { return false; }
        }

        public override object ReadJson(JsonReader reader, Type objectType,
                                     object existingValue, JsonSerializer serializer)
        {
            throw new NotImplementedException();
        }
    }
}
