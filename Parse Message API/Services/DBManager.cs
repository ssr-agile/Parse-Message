using Microsoft.Extensions.Configuration;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using MySql.Data.MySqlClient;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using System.Data.SQLite;

namespace Parse_Message_API.Services
{
    public class DBManager
    {
        private readonly string _connectionString;
        private readonly string _dbType;

        public DBManager(IConfiguration configuration)
        {
            string activeDbKey = configuration.GetValue<string>("ActiveDatabase") ?? throw new ArgumentNullException("ActiveDatabase not set");

            _dbType = configuration.GetValue<string>($"Databases:{activeDbKey}:Type")?.ToLower() ?? throw new ArgumentNullException($"Database type for '{activeDbKey}' is missing.");
            _connectionString = configuration.GetValue<string>($"Databases:{activeDbKey}:ConnectionString") ?? throw new ArgumentNullException($"Connection string for '{activeDbKey}' is missing.");
        }

        private DbConnection GetConnection()
        {
            return _dbType switch
            {
                "oracle" => new OracleConnection(_connectionString),
                "postgres" => new NpgsqlConnection(_connectionString),
                "mysql" => new MySqlConnection(_connectionString),
                "sqlserver" => new SqlConnection(_connectionString),
                "sqlite" => new SQLiteConnection(_connectionString),
                _ => throw new NotSupportedException($"Database type '{_dbType}' is not supported."),
            };
        }

        public async Task<bool> CreateTableAsync(string createTableSql)
        {
            return await ExecuteNonQueryAsync(createTableSql);
        }

        public async Task<DataTable> FetchDataAsync(string sqlQuery)
        {
            var dataTable = new DataTable();

            try
            {
                using (var connection = GetConnection())
                {
                    await connection.OpenAsync();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = sqlQuery;
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            dataTable.Load(reader); // Load data into DataTable
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching data: {ex.Message}");
            }

            return dataTable;
        }

        public async Task<bool> InsertDataAsync(string sqlQuery)
        {
            return await ExecuteNonQueryAsync(sqlQuery);
        }

        public async Task<bool> DeleteDataAsync(string sqlQuery)
        {
            return await ExecuteNonQueryAsync(sqlQuery);
        }

        public async Task<int> UpdateDataAsync(string sqlQuery)
        {
            return await ExecuteNonQueryWithResultAsync(sqlQuery);
        }

        private async Task<bool> ExecuteNonQueryAsync(string sqlQuery)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    await connection.OpenAsync();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = sqlQuery;
                        return await command.ExecuteNonQueryAsync() > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing non-query: {ex.Message}");
                return false;
            }
        }

        private async Task<int> ExecuteNonQueryWithResultAsync(string sqlQuery)
        {
            try
            {
                using (var connection = GetConnection())
                {
                    await connection.OpenAsync();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = sqlQuery;
                        return await command.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing non-query with result: {ex.Message}");
                return -1;
            }
        }
    }
}
