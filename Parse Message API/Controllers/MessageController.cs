using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Parse_Message_API.Services;
using Parse_Message_API.Model;
using Parse_Message_API.Data;
using System.Text.Json;
using System.Data;

namespace Parse_Message_API.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class MessageController : ControllerBase
    {
        private readonly RedisCacheServices _cacheService;
        private readonly ILogger<MessageController> _logger;
        private readonly MessageProducer _messageProducer;
        private readonly ApiContext _context;
        private readonly DBManager _dbService;

        // CONSTRUCTOR
        public MessageController(DBManager dbService,ApiContext context, ILogger<MessageController> logger, RedisCacheServices cacheService, MessageProducer messageProducer)
        {
            _context = context;
            _cacheService = cacheService;
            _logger = logger;
            _messageProducer = messageProducer;
            _dbService = dbService;
        }

        // MODEL
        public class MessageRequest { public string? Message { get; set; } }

        // MESSAGE PARSING
        [HttpPost]
        public async Task<IActionResult> ParseMessage([FromBody] MessageRequest request)
        {
            if (request?.Message == null || string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest("Invalid request: Message cannot be empty.");
            }

            string message = request.Message;
            string cacheKey = $"parsed_message:{message}";

            try
            {
                // Check cache first
                var cachedResult = await _cacheService.GetCacheAsync(cacheKey);
                if (cachedResult != null)
                {
                    try
                    {
                        return Ok(System.Text.Json.JsonSerializer.Deserialize<Message>(cachedResult));
                    }
                    catch (JsonException)
                    {
                        _logger.LogWarning("Cache contains invalid data for key: {cacheKey}", cacheKey);
                        await _cacheService.RemoveCacheAsync(cacheKey); // Remove corrupted cache
                    }
                }

                // Regular expression patterns
                var patterns = new Dictionary<string, string>
                {
                    { "dateText", @"\b(today|tomorrow)\b|\b(on|this|next)\s(monday|tuesday|wednesday|thursday|friday|saturday|sunday|january|february|march|april|may|june|july|august|september|october|november|december)\s?(\d{1,2}(st|nd|rd|th)?)?" },
                    { "dateActual", @"\b\d{1,2}[-/]\d{1,2}[-/]\d{4}\b" },
                    { "time", @"(\b\d{1,2}(:\d{2})?\s?(AM|PM)\b)|(\b([0-9]?|[01]?\d|2[0-3]):[0-5]\d\b)|(\b\d{1,2}(:\d{2})?\s?o['’]?clock\b)" },
                    { "title", @"`([^`]+)`" },
                    { "repeat", @"\b(repeat)\b" },
                    { "app", @"\b(Teams|Gmail|Email|WhatsApp|SMS|mail)\b" },
                    { "users", @"(send\s+to|to|remind|for|notify|message|alert|tell|inform|send(?:\s+a\s+message)?)\s+([a-zA-Z]+(?:\s*,\s*[a-zA-Z]+)*(\s+and\s+[a-zA-Z]+)?)\b" }
                };

                // Extract matches using regex
                var matches = patterns.ToDictionary(p => p.Key, p => Regex.Match(message, p.Value, RegexOptions.IgnoreCase | RegexOptions.Compiled));

                if (!matches["title"].Success)
                {
                    return BadRequest("Missing required field: Title.");
                }
                if (!matches["time"].Success)
                {
                    return BadRequest("Missing required field: Time.");
                }
                string[] SplitNames = Regex.Split(matches["users"].Groups[2].Value, @"\s*,\s*|\s+and\s+");

                // Extract users if available
                var extractedUsernames = matches["users"].Success
                    ? SplitNames
                        .Select(name => name.Trim().ToLower()) // Normalize to lowercase
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToArray()
                    : Array.Empty<string>();

                List<AxUsers> foundUsers = new();
                List<string> notFoundUsers = new();

                if (extractedUsernames.Length > 0)
                {
                    // Fetch data from DB (returns DataTable)
                    var dataTable = await _dbService.FetchDataAsync("SELECT * FROM tms.axusers");

                    // Convert DataTable to List<Dictionary<string, object>>
                    var userData = ConvertDataTableToList(dataTable);

                    // Convert data to List<AxUsers>
                    foundUsers = userData
                        .Select(row => new AxUsers
                        {
                            axusersid = row.ContainsKey("axusersid") && row["axusersid"] != null
                                ? Convert.ToInt64(row["axusersid"])
                                : 0,

                            username = row.ContainsKey("username") && row["username"] != null
                                ? row["username"].ToString()
                                : "",

                            email = row.ContainsKey("email") && row["email"] != null
                                ? row["email"].ToString()
                                : null,

                            mobile = row.ContainsKey("mobile") && row["mobile"] != null
                                ? row["mobile"].ToString()
                                : null
                        })
                        .ToList();

                    // Convert usernames to lowercase for case-insensitive comparison
                    HashSet<string> extractedSet = extractedUsernames.Select(u => u.ToLower()).ToHashSet();

                    // Find users that match the extracted usernames (case-insensitive)
                    foundUsers = foundUsers.Where(u => extractedSet.Contains(u.username.ToLower())).ToList();

                    // Find usernames that were not found in the database
                    notFoundUsers = extractedSet.Except(foundUsers.Select(u => u.username.ToLower())).ToList();
                }

                // Return JSON response
                if (notFoundUsers.Count > 0)
                {
                    return BadRequest(JsonSerializer.Serialize(new { message = "Users not found", users = notFoundUsers }));
                }

                var msg = new Message
                {
                    Id = Guid.NewGuid(),
                    Title = matches["title"].Groups[1].Value,
                    Repeat = matches["repeat"].Success,
                    App = matches["app"].Success ? matches["app"].Value : "All",
                    Time = ConvertToTime24(matches["time"].Value),
                    Date = matches["dateActual"].Success
                        ? matches["dateActual"].Value
                        : matches["dateText"].Success
                            ? ConvertToDate(matches["dateText"].Value).ToString("dd-MM-yyyy")
                            : "Invalid Date",
                    Send_to = foundUsers.Count > 0 ? foundUsers : new List<AxUsers> { new() { username = "self", email = "sriram@gmail.com", mobile = "1234567890" } },
                    Created_At = DateTime.UtcNow,
                    Trigger_At = GetTriggerTime(ConvertToDate(matches["dateText"].Value), matches["time"].Value)
                };

                //// ✅ Return JSON
                //return Ok(JsonSerializer.Serialize(msg));

                // ✅ Utility Function: Convert DataTable to List<Dictionary<string, object>>
                List<Dictionary<string, object>> ConvertDataTableToList(DataTable dt)
                {
                    var list = new List<Dictionary<string, object>>();
                    foreach (DataRow row in dt.Rows)
                    {
                        var dict = new Dictionary<string, object>();
                        foreach (DataColumn col in dt.Columns)
                        {
                            dict[col.ColumnName] = row[col] != DBNull.Value ? row[col] : null;
                        }
                        list.Add(dict);
                    }
                    return list;
                }

                // Store in cache for 10 minutes
                await _cacheService.SetCacheAsync(cacheKey, JsonSerializer.Serialize(msg), TimeSpan.FromMinutes(10));

                return Ok(msg);
            }
            catch (ArgumentException ex)
            {
                return BadRequest($"Invalid argument: {ex.Message}");
            }
            catch (FormatException ex)
            {
                return BadRequest($"Invalid format: {ex.Message}");
            }
            catch (RegexMatchTimeoutException)
            {
                return StatusCode(500, "Error processing request: Regular expression match timed out.");
            }
            catch (TaskCanceledException)
            {
                return StatusCode(500, "The operation was canceled due to timeout.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error occurred while parsing the message.");
                return StatusCode(500, "An unexpected error occurred. Please try again.");
            }
        }

        // GET TRIGGER TIME
        private static DateTime GetTriggerTime(DateTime date, string time)
        {
            try
            {
                string time24 = ConvertToTime24(time);
                return DateTime.ParseExact($"{date:yyyy-MM-dd} {time24}", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException($"Invalid date/time format: {ex.Message}");
            }
            catch (Exception ex)
            {
                throw new Exception($"An unexpected error occurred while parsing the trigger time: {ex.Message}");
            }
        }

        // CONVERT TO DATE
        static DateTime ConvertToDate(string textDate)
        {
            DateTime today = DateTime.Today;
            DayOfWeek todayDay = today.DayOfWeek;
            string[] months = DateTimeFormatInfo.CurrentInfo.MonthNames;
            string[] words = textDate.Split(' ');

            if (words.Length > 1 && (words[0] == "this" || words[0] == "next" || words[0] == "on"))
            {
                string type = words[0]; // "this", "next", "on"
                string secondWord = words[1];

                // Handle day-of-week cases
                if (Regex.IsMatch(secondWord, "monday|tuesday|wednesday|thursday|friday|saturday|sunday", RegexOptions.IgnoreCase))
                {
                    DayOfWeek targetDay = (DayOfWeek)Enum.Parse(typeof(DayOfWeek), CultureInfo.CurrentCulture.TextInfo.ToTitleCase(secondWord));

                    if (type == "this") return GetNextDayOfWeek(today, targetDay, includeToday: true);
                    if (type == "next") return GetNextDayOfWeek(today, targetDay, includeToday: false);
                    if (type == "on") return GetNextDayOfWeek(today, targetDay, includeToday: true);
                }

                // Handle month cases (with or without a day)
                if (Array.Exists(months, month => month.Equals(secondWord, StringComparison.OrdinalIgnoreCase)))
                {
                    int month = DateTime.ParseExact(secondWord, "MMMM", CultureInfo.CurrentCulture).Month;
                    int year = today.Year;
                    int day = 1; // Default to the 1st of the month

                    if (words.Length == 3)
                    {
                        string dayPart = words[2].Replace("st", "").Replace("nd", "").Replace("rd", "").Replace("th", "");
                        if (int.TryParse(dayPart, out int parsedDay))
                        {
                            day = parsedDay;
                        }
                    }

                    // Adjust year if needed
                    if (type == "next" || (month < today.Month && type != "this"))
                    {
                        year++;
                    }

                    // Validate day (e.g., prevent "February 30")
                    day = Math.Min(day, DateTime.DaysInMonth(year, month));

                    return new DateTime(year, month, day);
                }
            }

            return textDate switch
            {
                "today" => today,
                "tomorrow" => today.AddDays(1),
                "this week" => today.AddDays(-(int)todayDay), // Start of current week (Sunday)
                "next week" => today.AddDays(7 - (int)todayDay), // Start of next week (Sunday)
                "weekend" => today.AddDays(6 - (int)todayDay), // Next Saturday
                "this month" => new DateTime(today.Year, today.Month, 1), // 1st of current month
                "next month" => new DateTime(today.Year, today.Month, 1).AddMonths(1), // 1st of next month
                "this year" => new DateTime(today.Year, 1, 1), // Start of current year
                "next year" => new DateTime(today.Year + 1, 1, 1), // Start of next year
                _ => today // Default fallback
            };
        }
        // CONVERT TO TIME
        private static string ConvertToTime24(string time)
        {
            // Normalize different formats
            time = Regex.Replace(time, @"(\d)(AM|PM)", "$1 $2", RegexOptions.IgnoreCase); // Fix "3pm" → "3 pm"
            time = Regex.Replace(time, @"o['’]?clock", "", RegexOptions.IgnoreCase).Trim(); // Remove "o'clock"

            // If it's a single number (e.g., "3"), assume it's an hour and add ":00"
            if (Regex.IsMatch(time, @"^\d{1,2}$"))
            {
                if (Regex.IsMatch(time, @"^\d{1}$"))
                {
                    time = "0" + time;
                }
                time += ":00";

            }

            // Try parsing in multiple common formats
            return DateTime.TryParseExact(time,
                new[] { "h tt", "h:mm tt", "HH:mm", "h", "h:mm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt)
                ? dt.ToString("HH:mm:ss")
                : throw new FormatException("Invalid time format.");
        }

        // GET NEXT DAY OF WEEK
        private static DateTime GetNextDayOfWeek(DateTime start, DayOfWeek targetDay, bool includeToday)
        {
            int daysToAdd = ((int)targetDay - (int)start.DayOfWeek + 7) % 7;
            if (daysToAdd == 0 && !includeToday) daysToAdd = 7;
            return start.AddDays(daysToAdd);
        }

        // SEND DELY MESSAGE
        [HttpPost("sendDely")]
        public IActionResult SendMessage([FromBody] Message req)
        {
            if (req == null)
                return BadRequest("Invalid request data");

            _messageProducer.SendMessage(req.Id, req.Title, req.Date, req.Time, req.App, req.Repeat, req.Created_At, req.Trigger_At, req.Send_to);
            return Ok($"Message '{req.Title}' scheduled successfully for {req.Time}.");
        }
    }
}
