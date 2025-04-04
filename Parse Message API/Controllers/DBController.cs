using Microsoft.AspNetCore.Mvc;
using Parse_Message_API.Services;
using System;
using System.Data;
using System.Threading.Tasks;

namespace Parse_Message_API.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DBController : ControllerBase
    {
        private readonly DBManager _dbService;

        public DBController(DBManager dbService)
        {
            _dbService = dbService;
        }

        [HttpPost("create-table")]
        public async Task<IActionResult> CreateTable()
        {
            string sql = @"
        CREATE TABLE Employees (
            Id NUMBER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            Name VARCHAR2(100),
            Email VARCHAR2(100) UNIQUE,
            CreatedAt TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )";

            bool isCreated = await _dbService.CreateTableAsync(sql);
            return isCreated ? Ok("Table created successfully!") : BadRequest("Failed to create table.");
        }

        [HttpPost("fetch")]
        public async Task<IActionResult> FetchData([FromBody] string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return BadRequest("SQL query cannot be empty.");

            try
            {
                var dataTable = await _dbService.FetchDataAsync(sql);

                // Convert DataTable to serializable format
                var result = new
                {
                    Columns = dataTable.Columns.Cast<DataColumn>()
                        .Select(c => new { c.ColumnName, Type = c.DataType.Name }),
                    Rows = dataTable.AsEnumerable()
                        .Select(row => dataTable.Columns.Cast<DataColumn>()
                            .ToDictionary(col => col.ColumnName, col => row[col]))
                };

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error fetching data: {ex.Message}");
            }
        }

        [HttpPost("insert")]
        public async Task<IActionResult> InsertData([FromBody] string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return BadRequest("SQL query cannot be empty.");

            try
            {
                bool success = await _dbService.InsertDataAsync(sql);
                return success ? Ok(new { success }) : BadRequest("Insert failed.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error inserting data: {ex.Message}");
            }
        }

        [HttpDelete("delete")]
        public async Task<IActionResult> DeleteData([FromBody] string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return BadRequest("SQL query cannot be empty.");

            try
            {
                bool success = await _dbService.DeleteDataAsync(sql);
                return success ? Ok(new { success }) : BadRequest("Delete failed.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error deleting data: {ex.Message}");
            }
        }

        [HttpPut("update")]
        public async Task<IActionResult> UpdateData([FromBody] string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return BadRequest("SQL query cannot be empty.");

            try
            {
                int rowsAffected = await _dbService.UpdateDataAsync(sql);
                return rowsAffected > 0
                    ? Ok(new { rowsAffected })
                    : BadRequest("No rows updated.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error updating data: {ex.Message}");
            }
        }
    }
}
