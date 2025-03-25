using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Parse_Message_API.Model
{
    public class Message
    {
        [Key]
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public bool Repeat { get; set; }
        public string App { get; set; } = "All";
        public string Time { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public DateTime Trigger_At { get; set; }
        public DateTime Created_At { get; set; }

        [NotMapped] // Exclude from EF Core mapping
        public List<AxUsers> Send_to { get; set; }


    }
}
