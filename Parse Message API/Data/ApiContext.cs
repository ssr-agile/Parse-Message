using Microsoft.EntityFrameworkCore;
using Parse_Message_API.Model;

namespace Parse_Message_API.Data
{
    public class ApiContext : DbContext
    {
        public ApiContext(DbContextOptions<ApiContext> options) : base(options) { }

        public DbSet<Message> Messages { get; set; }
        public DbSet<AxUsers> AxUsers { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AxUsers>().ToTable("axusers", schema: "tms"); // ✅ Ensure correct table mapping
        }

    }
}
