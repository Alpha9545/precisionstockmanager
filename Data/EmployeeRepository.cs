using PlantStockManager.Models;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;

namespace PlantStockManager.Data
{
    public class EmployeeRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public EmployeeRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<List<Employee>> GetAllEmployees()
        {
            var employees = new List<Employee>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("SELECT * FROM Employee ORDER BY ID", conn);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        employees.Add(new Employee
                        {
                            EmployeeID = reader.GetInt32(0),
                            Name = reader.GetString(1),
                            Designation = reader.GetString(2)
                        });
                    }
                }
            }
            return employees;
        }

        public async Task<List<Employee>> GetAllEmployeesBooking()
        {
            var employees = new List<Employee>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand(" select i.id as EmployeeID, i.Name as Name, d.DesignationName from IMSUsers i join Designation d ON i.DesignationID = d.DesignationID  where d.DesignationName = 'Booking Executive' ORDER BY ID", conn);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        employees.Add(new Employee
                        {
                            EmployeeID = reader.GetInt32(0),
                            Name = reader.GetString(1),
                            Designation = reader.GetString(2)
                        });
                    }
                }
            }
            return employees;
        }

        public async Task<List<Employee>> GetAllEmployeesSowing()
        {
            var employees = new List<Employee>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("select i.id as EmployeeID, i.Name as Name, d.DesignationName from IMSUsers i join Designation d ON i.DesignationID = d.DesignationID  where d.DesignationName = 'Sowing Operator' ORDER BY ID", conn);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        employees.Add(new Employee
                        {
                            EmployeeID = reader.GetInt32(0),
                            Name = reader.GetString(1),
                            Designation = reader.GetString(2)
                        });
                    }
                }
            }
            return employees;
        }



        // General-purpose "any active user" list for new modules (Mother Plant
        // ResponsiblePersonId, etc.) that aren't restricted to one Designation
        // the way GetAllEmployeesBooking/GetAllEmployeesSowing are. Reuses the
        // existing IMSUsers/Designation tables -- no new user table.
        public async Task<List<Employee>> GetAllActiveUsers()
        {
            var employees = new List<Employee>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand(@"
                    SELECT i.Id AS EmployeeID, i.Name AS Name, ISNULL(d.DesignationName, '') AS Designation
                    FROM IMSUsers i
                    LEFT JOIN Designation d ON i.DesignationID = d.DesignationID
                    WHERE i.IsActive = 1
                    ORDER BY i.Name", conn);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        employees.Add(new Employee
                        {
                            EmployeeID = reader.GetInt32(0),
                            Name = reader.GetString(1),
                            Designation = reader.GetString(2)
                        });
                    }
                }
            }
            return employees;
        }

        public async Task AddEmployee(string name, string designation)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("INSERT INTO Employee (Name, Designation) VALUES (@Name, @Designation)", conn);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@Designation", designation);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task UpdateEmployee(int employeeID, string name, string designation)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("UPDATE Employee SET Name = @Name, Designation = @Designation WHERE ID = @EmployeeID", conn);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@Designation", designation);
                cmd.Parameters.AddWithValue("@EmployeeID", employeeID);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }
}
