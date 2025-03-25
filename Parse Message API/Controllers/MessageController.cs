using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Parse_Message_API.Services;
using Parse_Message_API.Model;
using Parse_Message_API.Data;
using System.Text.Json;

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

        // CONSTRUCTOR
        public MessageController(ApiContext context, ILogger<MessageController> logger, RedisCacheServices cacheService, MessageProducer messageProducer)
        {
            _context = context;
            _cacheService = cacheService;
            _logger = logger;
            _messageProducer = messageProducer;
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
                    { "time", @"(\b\d{1,2}(:\d{2})?\s*(AM|PM)\b)|(\b([01]?\d|2[0-3]):[0-5]\d\b)|(\b\d{1,2}\s?o['’]clock\b)" },
                    { "title", @"`([^`]+)`" },
                    { "repeat", @"\b(repeat)\b" },
                    { "app", @"\b(Teams|Gmail|Email|WhatsApp|SMS|mail)\b" },
                    { "users", @"(to|remind)\s+([a-zA-Z]+(?:,\s*[a-zA-Z]+)*)" }
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

                // Extract users if available
                var extractedUsernames = matches["users"].Success
                    ? matches["users"].Groups[2].Value
                        .Split(',')
                        .Select(name => name.Trim().ToLower()) // Normalize to lowercase
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToArray()
                    : Array.Empty<string>();

                List<AxUsers> foundUsers = new();
                List<string> notFoundUsers = new();

                if (extractedUsernames.Length > 0)
                {
                    // Fetch users in a case-insensitive manner
                    foundUsers = await _context.AxUsers
                        .AsNoTracking()
                        .Where(u => extractedUsernames.Contains(u.username.ToLower()))
                        .ToListAsync();

                    // Find usernames that were not found in the database
                    notFoundUsers = extractedUsernames.Except(foundUsers.Select(u => u.username.ToLower())).ToList();
                }


                if (notFoundUsers.Count > 0)
                {
                    return BadRequest($"Users not found: {string.Join(", ", notFoundUsers)}");
                }

                var msg = new Message
                {
                    Id = Guid.NewGuid(),
                    Title = matches["title"].Groups[1].Value, // Extract title text
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

                // Store in cache for 10 minutes
                await _cacheService.SetCacheAsync(cacheKey, System.Text.Json.JsonSerializer.Serialize(msg), TimeSpan.FromMinutes(10));

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
        private static DateTime ConvertToDate(string textDate)
        {
            DateTime today = DateTime.Today;
            string[] months = CultureInfo.CurrentCulture.DateTimeFormat.MonthNames;
            string[] words = textDate.Split(' ');

            try
            {
                if (textDate.Equals("today", StringComparison.OrdinalIgnoreCase))
                    return today;
                else if (textDate.Equals("tomorrow", StringComparison.OrdinalIgnoreCase))
                    return today.AddDays(1);

                if (words.Length > 1 && (words[0].Equals("this", StringComparison.OrdinalIgnoreCase) || words[0].Equals("next", StringComparison.OrdinalIgnoreCase) || words[0].Equals("on", StringComparison.OrdinalIgnoreCase)))
                {
                    if (Enum.TryParse(words[1], true, out DayOfWeek targetDay))
                    {
                        return GetNextDayOfWeek(today, targetDay, words[0].Equals("this", StringComparison.OrdinalIgnoreCase));
                    }

                    if (Array.Exists(months, month => month.Equals(words[1], StringComparison.OrdinalIgnoreCase)))
                    {
                        int year = today.Year;
                        int month = Array.IndexOf(months, words[1]) + 1;
                        int day = 1;

                        if (words.Length == 3 && int.TryParse(Regex.Replace(words[2], "[^0-9]", ""), out int parsedDay))
                            day = parsedDay;

                        if (words[0].Equals("next", StringComparison.OrdinalIgnoreCase) || (month < today.Month && !words[0].Equals("this", StringComparison.OrdinalIgnoreCase)))
                            year++;

                        return new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
                    }
                    throw new ArgumentException("Invalid date format.");
                }
                throw new ArgumentException("Could not parse date.");
            }
            catch (Exception ex)
            {
                throw new FormatException($"Error parsing date: {ex.Message}");
            }
        }

        // CONVERT TO TIME
        private static string ConvertToTime24(string time)
        {
            return DateTime.TryParseExact(time, new[] { "h tt", "h:mm tt", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt)
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
