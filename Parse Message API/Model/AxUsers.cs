namespace Parse_Message_API.Model
{
    public class AxUsers
    {
        public long axusersid { get; set; }  // Primary Key (Required for EF)
        public string username { get; set; }
        public string? email { get; set; }
        public string? mobile { get; set; }
    }
}
