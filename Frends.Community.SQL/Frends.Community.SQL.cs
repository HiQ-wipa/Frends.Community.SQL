﻿using CsvHelper;
using CsvHelper.Configuration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
#pragma warning disable 1591

namespace Frends.Community.SQL
{
    public static class SQL
    {
        /// <summary>
        /// Saves SQL query results to CSV file.
        /// </summary>
        /// <param name="parameters">Parameters of task</param>
        /// <param name="options">Additional options</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Returns amount of entries written</returns>
        public async static Task<int> SaveQueryToCSV([PropertyTab] SaveQueryToCSVParameters parameters, [PropertyTab] SaveQueryToCSVOptions options, CancellationToken cancellationToken)
        {
            var output = 0;
            var encoding = GetEncoding(options.FileEncoding, options.EnableBom, options.EncodingInString);

            using (var writer = new StreamWriter(parameters.OutputFilePath, false, encoding))
            using (var csvFile = CreateCsvWriter(options.GetFieldDelimeterAsString(), writer))
            using (var sqlConnection = new SqlConnection(parameters.ConnectionString))
            {
                writer.NewLine = options.GetLineBreakAsString();

                await sqlConnection.OpenAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                using (var command = BuildSQLCommand(parameters.Query, parameters.QueryParameters))
                {
                    command.CommandTimeout = parameters.TimeoutSeconds;
                    command.Connection = sqlConnection;

                    var reader = await command.ExecuteReaderAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    output = DataReaderToCsv(reader, csvFile, options, cancellationToken);
                }

                csvFile.Flush();
            }

            return output;
        }

        public static CsvWriter CreateCsvWriter(string delimeter, TextWriter writer)
        {
            var csvOptions = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                Delimiter = delimeter
            };

            return new CsvWriter(writer, csvOptions);
        }
        public static string FormatDbHeader(string header, bool forceSpecialFormatting)
        {
            if (!forceSpecialFormatting) return header;

            // First part of regex removes all non-alphanumeric ('_' also allowed) chars from the whole string.
            // Second part removed any leading numbers or underscoress.
            var rgx = new Regex("[^a-zA-Z0-9_-]|^[0-9_]+");
            header = rgx.Replace(header, "");
            return header.ToLower();
        }

        /// <summary>
        /// Formats a value according to options.
        /// </summary>
        /// <param name="value">Value from the database</param>
        /// <param name="dbTypeName">Type of database column. E.g. for differentiating between DATE and DATETIME types</param>
        /// <param name="dotnetType"></param>
        /// <param name="options">Formatting options</param>
        /// <returns></returns>
        public static string FormatDbValue(object value, string dbTypeName, Type dotnetType, SaveQueryToCSVOptions options)
        {
            if (value == null || value == DBNull.Value)
            {
                if (dotnetType == typeof(string)) return "\"\"";
                if (dotnetType == typeof(DateTime) && options.AddQuotesToDates) return "\"\"";
                return "";
            }

            if (dotnetType == typeof(string))
            {
                var str = (string)value;
                options.GetFieldDelimeterAsString();
                str = str.Replace("\"", "\\\"");
                str = str.Replace("\r\n", " ");
                str = str.Replace("\r", " ");
                str = str.Replace("\n", " ");
                return $"\"{str}\"";
            }

            if (dotnetType == typeof(DateTime))
            {
                var dateTime = (DateTime)value;
                var dbType = dbTypeName?.ToLower();
                string output;
                switch (dbType)
                {
                    case "date":
                        output = dateTime.ToString(options.DateFormat, CultureInfo.InvariantCulture);
                        break;
                    case "datetime":
                    default:
                        output = dateTime.ToString(options.DateTimeFormat, CultureInfo.InvariantCulture);
                        break;
                }

                if (options.AddQuotesToDates) return $"\"{output}\"";
                return output;
            }

            if (dotnetType == typeof(float))
            {
                var floatValue = (float)value;
                return floatValue.ToString("0.###########", CultureInfo.InvariantCulture);
            }

            if (dotnetType == typeof(double))
            {
                var doubleValue = (double)value;
                return doubleValue.ToString("0.###########", CultureInfo.InvariantCulture);
            }

            if (dotnetType == typeof(decimal))
            {
                var decimalValue = (decimal)value;
                return decimalValue.ToString("0.###########", CultureInfo.InvariantCulture);
            }

            return value.ToString();
        }

        public static int DataReaderToCsv(
            DbDataReader reader,
            CsvWriter csvWriter,
            SaveQueryToCSVOptions options,
            CancellationToken cancellationToken)
        {
            // Write header and remember column indexes to include.
            var columnIndexesToInclude = new List<int>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var columnName = reader.GetName(i);
                var includeColumn =
                    options.ColumnsToInclude == null ||
                    options.ColumnsToInclude.Length == 0 ||
                    options.ColumnsToInclude.Contains(columnName);

                if (includeColumn)
                {
                    if (options.IncludeHeadersInOutput)
                    {
                        var formattedHeader = FormatDbHeader(columnName, options.SanitizeColumnHeaders);
                        csvWriter.WriteField(formattedHeader);
                    }
                    columnIndexesToInclude.Add(i);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (options.IncludeHeadersInOutput) csvWriter.NextRecord();

            int count = 0;
            while (reader.Read())
            {
                foreach (var columnIndex in columnIndexesToInclude)
                {
                    var value = reader.GetValue(columnIndex);
                    var dbTypeName = reader.GetDataTypeName(columnIndex);
                    var dotnetType = reader.GetFieldType(columnIndex);
                    var formattedValue = FormatDbValue(value, dbTypeName, dotnetType, options);
                    csvWriter.WriteField(formattedValue, false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                csvWriter.NextRecord();
                count++;
            }

            return count;
        }

        private static SqlCommand BuildSQLCommand(string query, SQLParameter[] parmeters)
        {
            using (var command = new SqlCommand())
            {
                command.CommandText = query;
                command.CommandType = CommandType.Text;

                foreach (var parameter in parmeters)
                {
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                }

                return command;
            }
        }

        private static Encoding GetEncoding(FileEncoding optionsFileEncoding, bool optionsEnableBom, string optionsEncodingInString)
        {
            switch (optionsFileEncoding)
            {
                case FileEncoding.Other:
                    return Encoding.GetEncoding(optionsEncodingInString);
                case FileEncoding.ASCII:
                    return Encoding.ASCII;
                case FileEncoding.ANSI:
                    return Encoding.Default;
                case FileEncoding.UTF8:
                    return optionsEnableBom ? new UTF8Encoding(true) : new UTF8Encoding(false);
                case FileEncoding.Unicode:
                    return Encoding.Unicode;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }


        /// <summary>
        /// Bulk insert DataTable to a SQL table.
        /// </summary>
        /// <param name="input">Input parameters</param>
        /// <param name="options">Optional parameters with default values</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Copied row count</returns>
        public static async Task<int> BulkInsertDataTable([PropertyTab] BulkInsertInput input, [PropertyTab] BulkInsertOptions options, CancellationToken cancellationToken)
        {
            using (var connection = new SqlConnection(input.ConnectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                //Get the combined flags for multiple booleans that match a flag
                var flagEnum = options.FireTriggers.GetFlag(SqlBulkCopyOptions.FireTriggers) |
                                options.KeepIdentity.GetFlag(SqlBulkCopyOptions.KeepIdentity);

                if (options.SqlTransactionIsolationLevel == SqlTransactionIsolationLevel.None)
                {
                    using (var sqlBulkCopy = new SqlBulkCopy(connection.ConnectionString, flagEnum))
                    {
                        sqlBulkCopy.BulkCopyTimeout = options.CommandTimeoutSeconds;
                        sqlBulkCopy.DestinationTableName = input.TableName;

                        await sqlBulkCopy.WriteToServerAsync(input.InputData, cancellationToken).ConfigureAwait(false);

                        return sqlBulkCopy.RowsCopiedCount();
                    }
                }

                using (var transaction =
                    options.SqlTransactionIsolationLevel == SqlTransactionIsolationLevel.Default
                        ? connection.BeginTransaction()
                        : connection.BeginTransaction(options.SqlTransactionIsolationLevel.GetSqlTransactionIsolationLevel()))
                {


                    int rowsCopyCount;
                    using (var sqlBulkCopy = new SqlBulkCopy(connection, flagEnum, transaction))
                    {
                        sqlBulkCopy.BulkCopyTimeout = options.CommandTimeoutSeconds;
                        sqlBulkCopy.DestinationTableName = input.TableName;

                        await sqlBulkCopy.WriteToServerAsync(input.InputData, cancellationToken).ConfigureAwait(false);

                        rowsCopyCount = sqlBulkCopy.RowsCopiedCount();
                    }
                    transaction.Commit();
                    return rowsCopyCount;
                }
            }
        }

        // Cast Enum value to another Enum type by value name
        // Example: Frends.Community.SQL.SqlTransactionIsolationLevel -> System.Data.IsolationLevel
        // From https://github.com/FrendsPlatform/Frends.Sql/blob/master/Frends.Sql/Extensions.cs
        private static T GetEnum<T>(Enum enumValue)
        {
            return (T)Enum.Parse(typeof(T), enumValue.ToString());
        }
        // Cast Frends.Community.SQL.SqlTransactionIsolationLevel -> System.Data.IsolationLevel
        // From https://github.com/FrendsPlatform/Frends.Sql/blob/master/Frends.Sql/Extensions.cs
        internal static IsolationLevel GetSqlTransactionIsolationLevel(this SqlTransactionIsolationLevel sqlTransactionIsolationLevel)
        {
            return GetEnum<IsolationLevel>(sqlTransactionIsolationLevel);
        }
        // Return value or 0, depending on whether this bool is true or false, respectively.
        // From https://github.com/FrendsPlatform/Frends.Sql/blob/master/Frends.Sql/Extensions.cs
        public static T GetFlag<T>(this bool value, T flag)
        {
            return value ? flag : default(T);
        }
        // Get inserted row count with reflection
        // http://stackoverflow.com/a/12271001
        internal static int RowsCopiedCount(this SqlBulkCopy bulkCopy)
        {
            const string rowsCopiedFieldName = "_rowsCopied";
            FieldInfo rowsCopiedField = typeof(SqlBulkCopy).GetField(rowsCopiedFieldName,
                BindingFlags.NonPublic | BindingFlags.GetField | BindingFlags.Instance);
            return rowsCopiedField != null ? (int)rowsCopiedField.GetValue(bulkCopy) : 0;
        }
    }
}
