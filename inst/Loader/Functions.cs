﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace csv_to_sql_loader
{
    public static class Functions
    {   
        public static void InsertDataIntoSQLServerUsingSQLBulkCopy_2(DataTable dtable, string sqlTableName, Int32 batch_size, string connString)
        {
            try
            {
                using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connString, System.Data.SqlClient.SqlBulkCopyOptions.TableLock))
                {
                    bulkCopy.DestinationTableName = sqlTableName;

                    try
                    {
                        // Write from the source to the destination.
                        bulkCopy.BulkCopyTimeout = 0;
                        bulkCopy.BatchSize = batch_size;
                        bulkCopy.WriteToServer(dtable);
                    }
                    catch (SqlException ex)
                    {
                        if (ex.Message.Contains("Received an invalid column length from the bcp client for colid"))
                        {
                            string pattern = @"\d+";
                            Match match = Regex.Match(ex.Message.ToString(), pattern);
                            var index = Convert.ToInt32(match.Value) - 1;

                            FieldInfo fi = typeof(SqlBulkCopy).GetField("_sortedColumnMappings", BindingFlags.NonPublic | BindingFlags.Instance);
                            var sortedColumns = fi.GetValue(bulkCopy);
                            var items = (Object[])sortedColumns.GetType().GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(sortedColumns);

                            FieldInfo itemdata = items[index].GetType().GetField("_metadata", BindingFlags.NonPublic | BindingFlags.Instance);
                            var metadata = itemdata.GetValue(items[index]);

                            var column = metadata.GetType().GetField("column", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(metadata);
                            var length = metadata.GetType().GetField("length", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(metadata);

                            Console.WriteLine("Error message: Column [" + column + "] contains data with a length greater than " + length);
                            Console.WriteLine();
                            Console.WriteLine("Table " + sqlTableName + " already exists in DB, just change data type - see the tip below.");
                            Console.WriteLine("Tip: try something like ALTER TABLE table_name ALTER COLUMN column_name datatype;");
                            CleanUpTable(sqlTableName, connString);
                            Environment.Exit(0);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e.Message.ToString());
                        CleanUpTable(sqlTableName, connString);
                        Environment.Exit(0);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message.ToString());
                Environment.Exit(0);
            }
        }
        
        public static void CleanUpTable(string sqlTableName, string connString)
        {
            try
            {
                using (SqlConnection con = new SqlConnection(connString))
                {
                    con.Open();
                    string deleteRowsInTable = @"IF OBJECT_ID(" + "'" + sqlTableName + "','U')" +
                                                  " IS NOT NULL TRUNCATE TABLE " + sqlTableName + ";";
                    using (SqlCommand command = new SqlCommand(deleteRowsInTable, con))
                    {
                        command.CommandTimeout = 0;
                        command.ExecuteNonQuery();
                    }
                    con.Close();
                }
            }
            catch (SqlException sqlex)
            {
                Console.WriteLine("Truncate command cannot be used because of insufficient permissions: " + sqlex.Message.ToString());
                Console.WriteLine("DELETE FROM tabName is used instead.");
                using (SqlConnection con = new SqlConnection(connString))
                {
                    con.Open();
                    string deleteRowsInTable = @"IF OBJECT_ID(" + "'" + sqlTableName + "','U')" +
                                                  " IS NOT NULL DELETE FROM " + sqlTableName + ";";
                    using (SqlCommand command = new SqlCommand(deleteRowsInTable, con))
                    {
                        command.CommandTimeout = 0;
                        command.ExecuteNonQuery();
                    }
                    con.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message.ToString());
                Environment.Exit(0);
            }
        }

        public static void DropTable(string sqlTableName, string connString)
        {
            try
            {
                using (SqlConnection con = new SqlConnection(connString))
                {
                    con.Open();
                    string deleteRowsInTable = @"IF OBJECT_ID(" + "'" + sqlTableName + "','U')" +
                                                  " IS NOT NULL DROP TABLE " + sqlTableName + ";";
                    using (SqlCommand command = new SqlCommand(deleteRowsInTable, con))
                    {
                        command.CommandTimeout = 0;
                        command.ExecuteNonQuery();
                    }
                    con.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message.ToString());
                Environment.Exit(0);
            }
        }

        public static void SQLQueryTask(string sqltask, string connString)
        {
            try
            {
                using (SqlConnection con = new SqlConnection(connString))
                {
                    con.Open();
                    using (SqlCommand command = new SqlCommand(sqltask, con))
                    {
                        command.CommandTimeout = 0;
                        command.ExecuteNonQuery();
                    }
                    con.Close();
                    Console.WriteLine("Query has been completed!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message.ToString());
                Environment.Exit(0);
            }
        }
        
        public static string ReturnConStringName()
        {
            ConnectionStringSettingsCollection connections = ConfigurationManager.ConnectionStrings;
            string name = string.Empty;

            try
            {
                if (connections.Count != 0)
                {
                    foreach (ConnectionStringSettings connection in connections)
                    {
                        name = connection.Name;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message.ToString());
                Environment.Exit(0);
            }

            return name;
        }

        public static void ConvertCSVtoDataTable(string strFilePath, string tabName, Int32 flushed_batch_size,
                                                 bool showprogress, string connString, bool removeTab)
        {
            DataTable dt = new DataTable();
            Int64 rowsCount = 0;
            try
            {
                DataTable dataTypes = ExtractDataTypesFromSQLTable(tabName, connString);
                string str1 = string.Empty;
                int dt_rows_count = dataTypes.Rows.Count;

                char sep = '\t';

                using (StreamReader sr = new StreamReader(strFilePath))
                {
                    string[] headers = sr.ReadLine().Split(sep);

                    if (headers.Length != dt_rows_count)
                    {
                        Console.WriteLine("CSV file has different count of columns than table " + tabName + "!!!");
                        Console.WriteLine("Data Frame has " + headers.Length + " columns and table on SQL Server has " + dt_rows_count + " columns!");
                        Console.WriteLine("Tip: Try also check '" + sep + "' somewhere in the text in your DataFrame or DataTable you are trying to push to SQL Server,\nbecause tabulator is used as a separator!");
                        Environment.Exit(0);
                    }

                    // Compare header - CSV vs DataTable
                    for (int i = 0; i < dt_rows_count; i++)
                    {
                        DataRow drh = dataTypes.Rows[i];
                        if (headers[i].ToString().Replace("\"", "") != drh.ItemArray[0].ToString())
                        {
                            Console.WriteLine("You need to reorder columns in your csv according to columns in table " + tabName + "!!!");
                            Console.WriteLine("Column " + headers[i].ToString().Replace("\"", "") + " in your data.table or data.frame\ndoesn't correspond with column " + drh.ItemArray[0].ToString() + " defined in table " + tabName);
                            Environment.Exit(0);
                        }
                    }

                    if (removeTab)
                    {
                        Console.WriteLine("Cleaning table " + tabName);
                        CleanUpTable(tabName, connString);
                        Console.WriteLine("Table " + tabName + " has been cleaned");
                    }

                    for (int i = 0; i < dt_rows_count; i++)
                    {
                        DataRow dr = dataTypes.Rows[i];
                        // entire logic should goes here:
                        if (dr.ItemArray[1].ToString() == "float") { dt.Columns.Add(dr.ItemArray[0].ToString(), typeof(double)); }
                        else if (dr.ItemArray[1].ToString() == "real") { dt.Columns.Add(dr.ItemArray[0].ToString(), typeof(Single)); }
                        else if (dr.ItemArray[1].ToString() == "smallint") { dt.Columns.Add(dr.ItemArray[0].ToString(), typeof(Int16)); }
                        else if (dr.ItemArray[1].ToString() == "int") { dt.Columns.Add(dr.ItemArray[0].ToString(), typeof(Int32)); }
                        else if (dr.ItemArray[1].ToString() == "bigint") { dt.Columns.Add(dr.ItemArray[0].ToString(), typeof(Int64)); }
                        else if (dr.ItemArray[1].ToString() == "decimal" || dr.ItemArray[1].ToString() == "numeric") { dt.Columns.Add(dr.ItemArray[0].ToString(), typeof(decimal)); }
                        else { dt.Columns.Add(dr.ItemArray[0].ToString(), typeof(string)); }
                    }

                    Int64 batchsize = 0;

                    while (!sr.EndOfStream)
                    {
                        string[] rows = sr.ReadLine().Split(sep);

                        for (int i = 0; i < rows.Length; i++)
                        {
                            DataRow dtr = dataTypes.Rows[i];

                            if (rows[i] == "NA" || string.IsNullOrWhiteSpace(rows[i]))
                            {
                                rows[i] = null;
                            }
                            else
                            {
                                if (dtr.ItemArray[1].ToString() == "bigint") { rows[i] = Int64.Parse(rows[i], NumberStyles.Any).ToString(); }
                                else if (dtr.ItemArray[1].ToString() == "smallint") { rows[i] = Int16.Parse(rows[i], NumberStyles.Any).ToString(); }
                                else if (dtr.ItemArray[1].ToString() == "int") { rows[i] = Int32.Parse(rows[i], NumberStyles.Any).ToString(); }
                                else if (dtr.ItemArray[1].ToString() == "datetime") { rows[i] = DateTime.Parse(rows[i], null, DateTimeStyles.RoundtripKind).ToString(); }
                                else if (dtr.ItemArray[1].ToString() == "float") { rows[i] = double.Parse(rows[i], System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture).ToString(); }
                                else if (dtr.ItemArray[1].ToString() == "real") { rows[i] = Single.Parse(rows[i], System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture).ToString(); }
                                else if (dtr.ItemArray[1].ToString() == "decimal" || dtr.ItemArray[1].ToString() == "numeric") { rows[i] = Decimal.Parse(rows[i], System.Globalization.NumberStyles.Any, CultureInfo.InvariantCulture).ToString(); }
                                else { rows[i] = rows[i].ToString().Replace("\"",""); }
                            }
                        }

                        dt.Rows.Add(rows);
                        batchsize += 1;
                        
                        if (batchsize == flushed_batch_size)
                        {
                            InsertDataIntoSQLServerUsingSQLBulkCopy_2(dt, tabName, flushed_batch_size, connString);
                            dt.Rows.Clear();
                            batchsize = 0;
                            if (showprogress) { Console.WriteLine("Flushing " + flushed_batch_size + " rows (" + (rowsCount + 1) + " records already imported)"); }
                        }
                        rowsCount += 1;
                    }
                    InsertDataIntoSQLServerUsingSQLBulkCopy_2(dt, tabName, flushed_batch_size, connString);
                    dt.Rows.Clear();
                }
                Console.WriteLine(rowsCount + " records imported");
            }
            catch (FormatException fex)
            {
                Console.WriteLine(fex.Message.ToString());
                Console.WriteLine("Tip: there might be string between numeric data or the most likely escape character in string.\r\nCheck also scientific notation considered as string.");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message.ToString());
            }
        }

        public static DataTable ExtractDataTypesFromSQLTable(string tabName, string connString)
        {
            DataTable table = new DataTable();
            try
            {
                using (SqlConnection con = new SqlConnection(connString))
                {
                    string sqlQuery = @"SELECT [Column Name],[Data type],[Max Length],[precision],[scale],[is_nullable],[Primary Key]
                                        FROM
                                        (
                                        SELECT [Column Name],[Data type],[Max Length],[precision],[scale],[is_nullable],[Primary Key],
                                        r_number, ROW_NUMBER() OVER(PARTITION BY [Column Name] ORDER BY [Primary Key] DESC) rn
                                        FROM
                                        (
                                        SELECT
                                            c.name 'Column Name',
                                            t.Name 'Data type',
                                            c.max_length 'Max Length',
                                            c.[precision],
                                            c.[scale],
                                            c.is_nullable,
                                            ISNULL(i.is_primary_key, 0) 'Primary Key',
	                                        ROW_NUMBER() over(ORDER BY (SELECT NULL)) r_number
                                        FROM    
                                            sys.columns c
                                        INNER JOIN 
                                            sys.types t ON c.user_type_id = t.user_type_id
                                        LEFT OUTER JOIN 
                                            sys.index_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                                        LEFT OUTER JOIN 
                                            sys.indexes i ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                        WHERE
                                            c.object_id = OBJECT_ID('" + tabName + "')) a ) b WHERE b.rn = 1 ORDER BY b.r_number";

                    using (SqlCommand cmd = new SqlCommand(sqlQuery, con))
                    {
                        SqlDataAdapter ds = new SqlDataAdapter(cmd);
                        ds.Fill(table);
                    }
                    con.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message.ToString());
                Environment.Exit(0);
            }
            return table;
        }

        public static bool IfSQLTableExists(string tabname, string connString)
        {
            bool exists = false;
            tabname = tabname.Replace("[", string.Empty).Replace("]", string.Empty);
            try
            {
                using (SqlConnection con = new SqlConnection(connString))
                {
                    con.Open();
                    using (SqlCommand command = new SqlCommand("select case when exists((select * from information_schema.tables where TABLE_SCHEMA + '.' + table_name = '" + tabname + "' OR table_name = '" + tabname + "')) then 1 else 0 end", con))
                    {
                        command.CommandTimeout = 0;
                        exists = (int)command.ExecuteScalar() == 1;
                    }
                    con.Close();
                }
            }
            catch (InvalidOperationException ioe)
            {
                Console.WriteLine("Invalid Operation Exception: " + ioe.Message.ToString() + "\nSomething is wrong with your connection string! You might check back slashes!");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message.ToString());
                Environment.Exit(0);
            }
            return (exists);
        }

        public static DataTable GetDataTableFromDataReader(IDataReader dataReader)
        {
            DataTable schemaTable = dataReader.GetSchemaTable();
            DataTable resultTable = new DataTable();

            foreach (DataRow dataRow in schemaTable.Rows)
            {
                DataColumn dataColumn = new DataColumn();
                dataColumn.ColumnName = dataRow["ColumnName"].ToString();
                dataColumn.DataType = Type.GetType(dataRow["DataType"].ToString());
                dataColumn.ReadOnly = (bool)dataRow["IsReadOnly"];
                dataColumn.AutoIncrement = (bool)dataRow["IsAutoIncrement"];
                dataColumn.Unique = (bool)dataRow["IsUnique"];

                resultTable.Columns.Add(dataColumn);
            }
            while (dataReader.Read())
            {
                DataRow dataRow = resultTable.NewRow();
                for (int i = 0; i < resultTable.Columns.Count; i++)
                {
                    dataRow[i] = dataReader[i];
                }
                resultTable.Rows.Add(dataRow);
            }
            return resultTable;
        }

        // Write data into csv:
        public static void WriteFromDBToCSV(string sql_query, string csvpath, bool showprogress, string connString)
        {
            // DataTable dataTable = new DataTable();
            StringBuilder sb = new StringBuilder();
            DataTable dataTable = new DataTable();
            string sep = "~";

            try
            {
                using (SqlConnection con = new SqlConnection(connString))
                {
                    con.Open();
                    using (SqlCommand command = new SqlCommand(sql_query, con))
                    {
                        command.CommandTimeout = 0;

                        if (showprogress) { Console.WriteLine("Pushing data from SQL Server into DataTable"); }

                        using (IDataReader rdr = command.ExecuteReader())
                        {
                            dataTable = GetDataTableFromDataReader(rdr);
                        }

                        if (showprogress) { Console.WriteLine("Pushing data from DataTable object into StringBuilder"); }

                        for (int i = 0; i < dataTable.Columns.Count; i++)
                        {
                            sb.Append(dataTable.Columns[i].ColumnName);
                            sb.Append(i == dataTable.Columns.Count - 1 ? "\n" : sep);
                        }

                        string day_s = string.Empty;
                        string month_s = string.Empty;
                        string value = string.Empty;
                        Int32 counter = 0;
                        Int32 c_ounter = 0;
                        // Writing data into csv file
                        foreach (DataRow row in dataTable.Rows)
                        {
                            for (int i = 0; i < dataTable.Columns.Count; i++)
                            {
                                if (row[i].GetType().Name == "DateTime")
                                {
                                    DateTime dt_val = DateTime.Parse(row[i].ToString(), null, DateTimeStyles.RoundtripKind);
                                    if (dt_val.Month.ToString().Length == 1)
                                    {
                                        month_s = "0" + dt_val.Month.ToString();
                                    }
                                    else
                                    {
                                        month_s = dt_val.Month.ToString();
                                    }
                                    if (dt_val.Day.ToString().Length == 1)
                                    {
                                        day_s = "0" + dt_val.Day.ToString();
                                    }
                                    else
                                    {
                                        day_s = dt_val.Day.ToString();
                                    }
                                    value = dt_val.Year.ToString() + "-" + month_s + "-" + day_s + " " + dt_val.TimeOfDay.ToString();
                                    sb.Append(value);
                                    sb.Append(i == dataTable.Columns.Count - 1 ? "\n" : sep);
                                }
                                else if (row[i].GetType().Name == "Decimal" |
                                        row[i].GetType().Name == "Numeric" |
                                        row[i].GetType().Name == "Float" |
                                        row[i].GetType().Name == "Double" |
                                        row[i].GetType().Name == "Single")
                                {
                                    Double val;
                                    if (double.TryParse(row[i].ToString(), out val))
                                    {
                                        sb.Append(val.ToString(CultureInfo.InvariantCulture));
                                        sb.Append(i == dataTable.Columns.Count - 1 ? "\n" : sep);
                                    }
                                }
                                else
                                {
                                    sb.Append(row[i].ToString());
                                    sb.Append(i == dataTable.Columns.Count - 1 ? "\n" : sep);
                                }   
                            }
                            counter++;
                            c_ounter++;
                            if (c_ounter == 100000 & showprogress)
                            {
                                Console.WriteLine(counter + " rows inserted from StringBuilder --> csv.");
                                File.AppendAllText(csvpath, sb.ToString());
                                sb.Clear();
                                c_ounter = 0;
                            }
                        }
                        if (sb.Length != 0)
                        {
                            File.AppendAllText(csvpath, sb.ToString());
                        }
                        Console.WriteLine(counter + " records written into DataFrame/DataTable.");
                    }
                    con.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message.ToString());
                Environment.Exit(0);
            }
        }
        public static void CreateSQLTable(string pathtocsv, Int32 rowstoestimatedatatype, string tablename, string connstring)
        {
            char separator;
            using (StreamReader sr = new StreamReader(pathtocsv))
            {
                separator = '\t';
            }
            string[,] sqldts = DataTypeIdentifier.SQLDataTypes(pathtocsv, rowstoestimatedatatype, separator);

            string createTable_string = string.Empty;

            for (int i = 0; i < sqldts.GetLength(1); i++)
            {
                if (i == sqldts.GetLength(1)-1)
                {
                    createTable_string = createTable_string + "[" + sqldts[1, i] + "]" + " " + sqldts[0, i];
                }
                else
                {
                    createTable_string = createTable_string + "[" + sqldts[1, i] + "]" + " " + sqldts[0, i] + ", ";
                }
            }
            try
            {
                using (SqlConnection con = new SqlConnection(connstring))
                {
                    con.Open();
                    string createTable = @"CREATE TABLE " + tablename + " (" + createTable_string + ");";
                    using (SqlCommand command = new SqlCommand(createTable, con))
                    {
                        command.CommandTimeout = 0;
                        command.ExecuteNonQuery();
                    }
                    con.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message.ToString());
                Environment.Exit(0);
            }
        }
        public static Tuple<bool, string> IsServerConnected(string connectionString)
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                try
                {
                    connection.Open();
                    var tpl = new Tuple<bool, string>(true, string.Empty);
                    return tpl;
                }
                catch (SqlException ex)
                {
                    string msg = "SqlException message: " + ex.Message.ToString();
                    var tpl = new Tuple<bool, string>(false, msg);
                    return tpl;
                }
            }
        }
    }
}
