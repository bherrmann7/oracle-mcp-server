# Oracle MCP Server

An Oracle database MCP (Model Context Protocol) Server that provides Oracle database access tools through the MCP
protocol. This server acts as a bridge between MCP clients and Oracle databases, allowing clients to execute SQL queries
and database operations via standardized MCP tool calls.

## Features

- **Execute Queries**: Run SELECT statements and get JSON results
- **Execute Non-Queries**: Run INSERT/UPDATE/DELETE/DDL statements
- **Test Connections**: Verify database connectivity for configured schemas
- **List Schemas**: View available database connection configurations
- **Secure Credentials**: Connection strings stored separately from code
- **Multi-Schema Support**: Connect to multiple Oracle databases/schemas

## Prerequisites

- .NET 9.0 or later
- Oracle Database (any supported version)
- Oracle client libraries (automatically handled by Oracle.ManagedDataAccess.Core)

## Quick Start

### 1. Clone and Build

```bash
git clone <repository-url>
cd oracle-mcp-server
dotnet build
```

### 2. Configure Database Connections

Copy the example credentials file:

**Windows:**
```cmd
copy oracle-mcp-server-creds.json.example %USERPROFILE%\.oracle-mcp-server-creds.json
```

**Mac/Linux:**
```bash
cp oracle-mcp-server-creds.json.example ~/.oracle-mcp-server-creds.json
```

Edit the credentials file with your Oracle connection strings:

- **Windows:** `%USERPROFILE%\.oracle-mcp-server-creds.json`
- **Mac/Linux:** `~/.oracle-mcp-server-creds.json`

```json
{
  "ConnectionStrings": {
    "schema1": "Data Source=localhost:1521/testdb;User Id=user1;Password=pass1;",
    "schema2": "Data Source=localhost:1521/testdb;User Id=user2;Password=pass2;"
  }
}
```

Secure the credentials file:

**Windows:**
```cmd
icacls %USERPROFILE%\.oracle-mcp-server-creds.json /inheritance:r /grant:r %USERNAME%:F
```

**Mac/Linux:**
```bash
chmod 600 ~/.oracle-mcp-server-creds.json
```

### 3. Run the Server

```bash
dotnet run
```

The server will start and listen for MCP protocol messages on stdin/stdout.

## Available MCP Tools

| Tool                   | Description                                    | Parameters        |
|------------------------|------------------------------------------------|-------------------|
| `ExecuteQuery`         | Execute SELECT queries and return JSON results | `schema`, `query` |
| `ExecuteNonQuery`      | Execute INSERT/UPDATE/DELETE/DDL statements    | `schema`, `query` |
| `TestConnection`       | Test database connectivity for a schema        | `schema`          |
| `ListAvailableSchemas` | List configured connection strings/schemas     | None              |

### Example Tool Usage

**Execute a Query:**

```json
{
  "method": "tools/call",
  "params": {
    "name": "ExecuteQuery",
    "arguments": {
      "schema": "schema1",
      "query": "SELECT * FROM employees WHERE department = 'IT'"
    }
  }
}
```

**Test Connection:**

```json
{
  "method": "tools/call",
  "params": {
    "name": "TestConnection",
    "arguments": {
      "schema": "schema1"
    }
  }
}
```

## Configuration

### Connection Strings

The server loads connection strings from the user's home directory by default. If this file doesn't exist, it falls back to `appsettings.json`.

**Default credential file locations:**
- **Windows:** `%USERPROFILE%\.oracle-mcp-server-creds.json`
- **Mac/Linux:** `~/.oracle-mcp-server-creds.json`

**Credentials File Structure:**

```json
{
  "ConnectionStrings": {
    "schema_name": "Oracle_Connection_String"
  }
}
```

**Oracle Connection String Format:**

```
Data Source=hostname:port/service_name;User Id=username;Password=password;
```

### Adding New Schemas

1. Add a new entry to the `ConnectionStrings` section in your credentials file
2. Use the schema name as the key and the full Oracle connection string as the value
3. The schema name can then be used with any MCP tool

## Development

### Build

```bash
dotnet build
```

### Run Tests

```bash
dotnet test
```

### Run Specific Test

```bash
dotnet test --filter "TestMethodName"
```

## Security

- **Credentials Isolation**: Database credentials are stored in a separate file outside the codebase
- **File Permissions**: Secure your credentials file:
  - **Windows:** `icacls %USERPROFILE%\.oracle-mcp-server-creds.json /inheritance:r /grant:r %USERNAME%:F`
  - **Mac/Linux:** `chmod 600 ~/.oracle-mcp-server-creds.json`
- **No Credential Logging**: Connection strings are not logged or exposed in error messages
- **Parameterized Queries**: Use parameterized queries to prevent SQL injection

## Architecture

- **Program.cs**: Main entry point, sets up MCP server with stdio transport
- **OracleDatabaseTools.cs**: Contains all MCP tool implementations
- **ServerIntegrationTests.cs**: Integration tests for server functionality

Tools are implemented as static async methods decorated with `[McpServerTool]` and `[Description]` attributes. All
responses follow a consistent JSON format with success indicators and error handling.

## Troubleshooting

### Connection Issues

- Verify your Oracle connection string format
- Check that the Oracle database is accessible from your network
- Ensure the user has appropriate permissions for the operations you're trying to perform

### File Not Found Errors

- Make sure the credentials file exists and is readable:
  - **Windows:** `%USERPROFILE%\.oracle-mcp-server-creds.json`
  - **Mac/Linux:** `~/.oracle-mcp-server-creds.json`
- Check file permissions are set correctly for security

### MCP Protocol Issues

- Ensure stdout is only used for MCP protocol messages
- Check that the MCP client is sending properly formatted requests

## License

MIT
