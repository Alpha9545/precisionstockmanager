using System;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace PlantStockManager.Data
{
    public class DatabaseHelper
    {
       
        private readonly string _connectionString;

        public DatabaseHelper(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public SqlConnection GetConnection()
        {
            return new SqlConnection(_connectionString);
        }
    }
}
