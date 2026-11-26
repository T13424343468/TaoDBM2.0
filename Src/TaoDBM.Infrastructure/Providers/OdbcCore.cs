// ODBC 最小核心（批次四格3，2026-11-23）：odbc32.dll P/Invoke（SQLAllocHandle / SQLDriverConnect /
// SQLExecDirect / SQLFetch），自封连接 / 查询 / 元数据三层；不引第三方包。
// Windows 专用：非 Windows 或缺 odbc32 时调用即抛友好异常（连接路径不进 LogicTest）。
using System.Data;
using System.Data.Common;
using System.Runtime.InteropServices;
using System.Text;

namespace TaoDBM.Core;

/// <summary>odbc32 P/Invoke 与句柄封装：环境句柄进程级单例，连接 / 语句句柄随用随释放。</summary>
public static class OdbcCore
{
    private const string Lib = "odbc32";
    private const short SQL_HANDLE_ENV = 1;
    private const short SQL_HANDLE_DBC = 2;
    private const short SQL_HANDLE_STMT = 3;
    private const int SQL_ATTR_ODBC_VERSION = 200;
    private const int SQL_OV_ODBC3 = 3;
    private const short SQL_DRIVER_NOPROMPT = 0;
    private const short SQL_FETCH_FIRST = 2;
    private const short SQL_FETCH_NEXT = 1;
    private const short SQL_FETCH_FIRST_USER = 31;
    private const short SQL_FETCH_FIRST_SYSTEM = 32;
    private const short SQL_SUCCESS = 0;
    private const short SQL_SUCCESS_WITH_INFO = 1;
    private const short SQL_NO_DATA = 100;
    private const int SQL_NULL_HANDLE = 0;

    [DllImport(Lib, SetLastError = true)]
    private static extern short SQLAllocHandle(short handleType, IntPtr inputHandle, out IntPtr outputHandle);

    [DllImport(Lib, SetLastError = true)]
    private static extern short SQLFreeHandle(short handleType, IntPtr handle);

    [DllImport(Lib, SetLastError = true)]
    private static extern short SQLSetEnvAttr(IntPtr env, int attr, IntPtr value, int strLen);

    [DllImport(Lib, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern short SQLDriverConnectW(IntPtr dbc, IntPtr windowHandle, [MarshalAs(UnmanagedType.LPWStr)] string connStr, short connStrLen, StringBuilder? outConnStr, short outLen, out short outLenPtr, short driverCompletion);

    [DllImport(Lib, SetLastError = true)]
    private static extern short SQLDisconnect(IntPtr dbc);

    [DllImport(Lib, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern short SQLExecDirectW(IntPtr stmt, [MarshalAs(UnmanagedType.LPWStr)] string sql, int sqlLen);

    [DllImport(Lib, SetLastError = true)]
    private static extern short SQLFetch(IntPtr stmt);

    [DllImport(Lib, SetLastError = true)]
    private static extern short SQLNumResultCols(IntPtr stmt, out short colCount);

    [DllImport(Lib, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern short SQLDescribeColW(IntPtr stmt, short col, StringBuilder name, short nameLen, out short nameLenPtr, out short dataType, out nint colSize, out short decimalDigits, out short nullable);

    [DllImport(Lib, SetLastError = true)]
    private static extern short SQLRowCount(IntPtr stmt, out nint rowCount);

    [DllImport(Lib, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern short SQLDataSourcesW(IntPtr env, short direction, StringBuilder name, short nameLen, out short nameLenPtr, StringBuilder? desc, short descLen, out short descLenPtr);

    [DllImport(Lib, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern short SQLGetDiagRecW(short handleType, IntPtr handle, short recNum, StringBuilder state, out int nativeError, StringBuilder msg, short msgLen, out short msgLenPtr);

    private static IntPtr _env = IntPtr.Zero;
    private static readonly object EnvGate = new();

    private static IntPtr Env
    {
        get
        {
            lock (EnvGate)
            {
                if (_env == IntPtr.Zero)
                {
                    Check(SQLAllocHandle(SQL_HANDLE_ENV, IntPtr.Zero, out _env), IntPtr.Zero, "分配 ODBC 环境");
                    Check(SQLSetEnvAttr(_env, SQL_ATTR_ODBC_VERSION, (IntPtr)SQL_OV_ODBC3, 0), _env, "设置 ODBC 版本");
                }
                return _env;
            }
        }
    }

    private static void Check(short ret, IntPtr handle, string what)
    {
        if (ret == SQL_SUCCESS || ret == SQL_SUCCESS_WITH_INFO) return;
        throw new OdbcException2(what, handle, handleTypeOf(what));
    }

    private static short handleTypeOf(string what) =>
        what.Contains("语句") ? SQL_HANDLE_STMT : what.Contains("连接") ? SQL_HANDLE_DBC : SQL_HANDLE_ENV;

    /// <summary>ODBC 异常：带诊断状态与消息（SQLGetDiagRec）。</summary>
    public sealed class OdbcException2 : Exception
    {
        public OdbcException2(string what, IntPtr handle, short handleType)
            : base(Describe(what, handle, handleType)) { }

        private static string Describe(string what, IntPtr handle, short handleType)
        {
            if (handle == IntPtr.Zero) return what + " 失败（无诊断信息）";
            var state = new StringBuilder(6);
            var msg = new StringBuilder(512);
            return SQLGetDiagRecW(handleType, handle, 1, state, out _, msg, (short)msg.Capacity, out _) == SQL_SUCCESS
                ? $"{what} 失败：[{state}] {msg}"
                : what + " 失败（无诊断信息）";
        }
    }

    /// <summary>枚举本机数据源（用户 + 系统 DSN）。</summary>
    public static List<string> ListDsns()
    {
        var list = new List<string>();
        var name = new StringBuilder(256);
        var desc = new StringBuilder(256);
        foreach (var start in new[] { SQL_FETCH_FIRST_USER, SQL_FETCH_FIRST_SYSTEM })
        {
            short dir = start;
            while (true)
            {
                var ret = SQLDataSourcesW(Env, dir, name, (short)name.Capacity, out var nLen, desc, (short)desc.Capacity, out _);
                if (ret != SQL_SUCCESS && ret != SQL_SUCCESS_WITH_INFO) break;
                var n = name.ToString(0, nLen);
                if (!list.Contains(n)) list.Add(n);
                dir = SQL_FETCH_NEXT;
            }
        }
        return list;
    }

    /// <summary>由连接档案构造 ODBC 连接串：DSN + 账号（Database 存 DSN 名，Parameters 可带 k=v 附加）。</summary>
    public static string BuildConnectionString(ConnectionProfile p, string? db = null)
    {
        var dsn = db is { Length: > 0 } ? db : p.Database;
        var sb = new StringBuilder("DSN=").Append(EscapeValue(dsn));
        if (p.User.Length > 0) sb.Append(";UID=").Append(EscapeValue(p.User));
        if (p.Password.Length > 0) sb.Append(";PWD=").Append(EscapeValue(p.Password));
        if (p.Parameters.Length > 0)
            foreach (var kv in p.Parameters.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                sb.Append(';').Append(kv);
        return sb.ToString();
    }

    /// <summary>ODBC 连接串值转义（发现101）：分号/花括号等特殊字符包 {} 并对内层 } 翻倍，普通值原样。</summary>
    internal static string EscapeValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (value.AsSpan().IndexOfAny(new[] { ';', '{', '}', '=', '(', ')', ',', '\'', '"' }) < 0) return value;
        return "{" + value.Replace("}", "}}") + "}";
    }

    /// <summary>ODBC 连接：SQLDriverConnect（无窗口提示），Dispose 断开。</summary>
    public sealed class OdbcDbConnection : DbConnection
    {
        private IntPtr _dbc;
        private string _connStr;
        private ConnectionState _state = ConnectionState.Closed;

        public OdbcDbConnection(string connStr) => _connStr = connStr;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString
        {
            get => _connStr;
            set => _connStr = value ?? "";
        }

        public override string Database => "";
        public override string DataSource => "ODBC";
        public override string ServerVersion => "";
        public override ConnectionState State => _state;

        internal IntPtr Handle => _dbc;

        public override void ChangeDatabase(string databaseName) { }   // 最小实现：DSN 已定库，不切库

        private static void Check(short ret, string what, IntPtr handle)
        {
            if (ret == SQL_SUCCESS || ret == SQL_SUCCESS_WITH_INFO) return;
            throw new OdbcException2(what, handle, SQL_HANDLE_DBC);
        }

        public override void Open()
        {
            if (_state == ConnectionState.Open) return;
            Check(SQLAllocHandle(SQL_HANDLE_DBC, Env, out _dbc), "分配 ODBC 连接", IntPtr.Zero);
            Check(SQLDriverConnectW(_dbc, IntPtr.Zero, _connStr, (short)_connStr.Length, null, 0, out _, SQL_DRIVER_NOPROMPT),
                "打开 ODBC 数据源", _dbc);
            _state = ConnectionState.Open;
        }

        public override void Close()
        {
            if (_dbc != IntPtr.Zero)
            {
                SQLDisconnect(_dbc);
                SQLFreeHandle(SQL_HANDLE_DBC, _dbc);
                _dbc = IntPtr.Zero;
            }
            _state = ConnectionState.Closed;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel il) => throw new NotSupportedException("ODBC 最小实现不支持事务。");
        protected override DbCommand CreateDbCommand() => new OdbcDbCommand { Connection = this };
        protected override void Dispose(bool disposing) { Close(); base.Dispose(disposing); }
    }

    /// <summary>ODBC 语句：ExecDirect + 逐行取数（SQLFetch / SQLDescribeCol / SQLGetData 经 reader）。</summary>
    public sealed class OdbcDbCommand : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        protected override DbConnection DbConnection { get; set; } = null!;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => new OdbcParameterCollection();
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException("ODBC 最小实现不支持参数。");

        public override void Prepare() { }
        public override void Cancel() { }
        public override int ExecuteNonQuery()
        {
            using var r = (OdbcDbReader)ExecuteDbDataReader(CommandBehavior.Default);
            return r.RecordsAffected;
        }
        public override object? ExecuteScalar()
        {
            using var r = (OdbcDbReader)ExecuteDbDataReader(CommandBehavior.Default);
            return r.Read() ? r.GetValue(0) : null;
        }
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var conn = (OdbcDbConnection)DbConnection;
            if (conn.State != ConnectionState.Open) throw new InvalidOperationException("连接未打开。");
            Check(SQLAllocHandle(SQL_HANDLE_STMT, conn.Handle, out var stmt), "分配 ODBC 语句", IntPtr.Zero);
            try
            {
                Check(SQLExecDirectW(stmt, CommandText, CommandText.Length), "执行 ODBC 语句", stmt);
                return new OdbcDbReader(stmt);
            }
            catch
            {
                SQLFreeHandle(SQL_HANDLE_STMT, stmt);
                throw;
            }
        }

        private static void Check(short ret, string what, IntPtr handle)
        {
            if (ret == SQL_SUCCESS || ret == SQL_SUCCESS_WITH_INFO) return;
            throw new OdbcException2(what, handle, SQL_HANDLE_STMT);
        }
    }

    /// <summary>ODBC 结果读取：列元数据走 SQLDescribeColW，取值走 SQLGetData（字符 / 数值 / 二进制最小口径）。</summary>
    public sealed class OdbcDbReader : DbDataReader
    {
        private const short SQL_C_CHAR = 1;
        private const short SQL_C_WCHAR = -8;
        private const short SQL_C_BINARY = -2;
        private const short SQL_C_SLONG = -360;   // SQL_C_LONG signed
        private const short SQL_C_DOUBLE = 8;
        private const int SQL_NULL_DATA = -1;

        [DllImport(Lib, SetLastError = true)]
        private static extern short SQLGetData(IntPtr stmt, short col, short target, byte[] buf, int bufLen, out nint lenInd);

        private readonly IntPtr _stmt;
        private short _cols;
        private string[] _names = Array.Empty<string>();
        private bool _hasRow;
        private bool _finished;

        public OdbcDbReader(IntPtr stmt)
        {
            _stmt = stmt;
            SQLNumResultCols(_stmt, out _cols);
            _names = new string[_cols];
            for (short i = 0; i < _cols; i++)
            {
                var sb = new StringBuilder(256);
                SQLDescribeColW(_stmt, (short)(i + 1), sb, (short)sb.Capacity, out var n, out _, out _, out _, out _);
                _names[i] = sb.ToString(0, n);
            }
        }

        public override int FieldCount => _cols;
        public override int Depth => 0;
        public override bool HasRows => _hasRow || !_finished;
        public override bool IsClosed => false;
        private int _recordsAffected;
        public override int RecordsAffected => _recordsAffected;
        public override object this[int i] => GetValue(i);
        public override object this[string name] => GetValue(GetOrdinal(name));

        public override string GetName(int i) => _names[i];
        public override int GetOrdinal(string name)
        {
            for (int i = 0; i < _names.Length; i++)
                if (_names[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new IndexOutOfRangeException("列不存在：" + name);
        }

        public override bool Read()
        {
            if (_finished) return false;
            var ret = SQLFetch(_stmt);
            if (ret == SQL_SUCCESS || ret == SQL_SUCCESS_WITH_INFO) { _hasRow = true; return true; }
            _finished = true;
            SQLRowCount(_stmt, out var affected);
            if (affected >= 0) _recordsAffected = (int)affected;
            return false;
        }

        public override bool NextResult() { _finished = true; return false; }

        public override object GetValue(int i)
        {
            // 最小口径：全部按 UTF-16 文本取（SQL_C_WCHAR），二进制按字节；NULL → DBNull
            var buf = new byte[8192];
            var ret = SQLGetData(_stmt, (short)(i + 1), SQL_C_WCHAR, buf, buf.Length, out var ind);
            if (ret == SQL_SUCCESS || ret == SQL_SUCCESS_WITH_INFO)
            {
                if (ind == SQL_NULL_DATA) return DBNull.Value;
                int len = ind >= 0 ? (int)ind : buf.Length;
                return Encoding.Unicode.GetString(buf, 0, Math.Min(len, buf.Length)).TrimEnd('\0');
            }
            if (ret == SQL_NO_DATA) return DBNull.Value;
            // 文本取失败（二进制列等）：退二进制
            ret = SQLGetData(_stmt, (short)(i + 1), SQL_C_BINARY, buf, buf.Length, out ind);
            if (ret == SQL_SUCCESS || ret == SQL_SUCCESS_WITH_INFO && ind != SQL_NULL_DATA)
            {
                int len = ind >= 0 ? (int)ind : 0;
                var bytes = new byte[len];
                Array.Copy(buf, bytes, len);
                return bytes;
            }
            return DBNull.Value;
        }

        public override bool GetBoolean(int i) => Convert.ToBoolean(GetValue(i));
        public override byte GetByte(int i) => Convert.ToByte(GetValue(i) ?? 0);
        public override Guid GetGuid(int i) => Guid.Parse(Convert.ToString(GetValue(i)) ?? Guid.Empty.ToString());
        public override long GetBytes(int i, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        {
            if (GetValue(i) is not byte[] b) return 0;
            if (buffer is null) return b.Length;
            int avail = Math.Max(0, b.Length - (int)dataOffset);
            int n = Math.Min(length, avail);
            Array.Copy(b, (int)dataOffset, buffer, bufferOffset, n);
            return n;
        }
        public override char GetChar(int i) => Convert.ToChar(GetValue(i) ?? " ");
        public override long GetChars(int i, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override string GetDataTypeName(int i) => "string";
        public override System.Collections.IEnumerator GetEnumerator() => new DbEnumerator(this);
        public override Type GetFieldType(int i) => GetValue(i) is byte[] ? typeof(byte[]) : typeof(string);
        public override System.Data.DataTable GetSchemaTable() => throw new NotSupportedException();
        public override int GetValues(object[] values)
        {
            for (int i = 0; i < _cols && i < values.Length; i++) values[i] = GetValue(i) ?? DBNull.Value;
            return Math.Min(_cols, values.Length);
        }
        public override bool IsDBNull(int i) => GetValue(i) is DBNull;
        public override DateTime GetDateTime(int i) => DateTime.TryParse(Convert.ToString(GetValue(i)), out var d) ? d : default;
        public override decimal GetDecimal(int i) => decimal.Parse(Convert.ToString(GetValue(i)) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        public override double GetDouble(int i) => double.Parse(Convert.ToString(GetValue(i)) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        public override float GetFloat(int i) => float.Parse(Convert.ToString(GetValue(i)) ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        public override short GetInt16(int i) => short.Parse(Convert.ToString(GetValue(i)) ?? "0");
        public override int GetInt32(int i) => int.Parse(Convert.ToString(GetValue(i)) ?? "0");
        public override long GetInt64(int i) => long.Parse(Convert.ToString(GetValue(i)) ?? "0");
        public override string GetString(int i) => Convert.ToString(GetValue(i)) ?? "";

        protected override void Dispose(bool disposing)
        {
            if (_stmt != IntPtr.Zero) { SQLFreeHandle(SQL_HANDLE_STMT, _stmt); }
            base.Dispose(disposing);
        }
    }

    /// <summary>参数集合占位（最小实现不支持参数化）。</summary>
    public sealed class OdbcParameterCollection : DbParameterCollection
    {
        public override int Count => 0;
        public override object SyncRoot => this;
        public override int Add(object value) => 0;
        public override void AddRange(Array values) { }
        public override void Clear() { }
        public override bool Contains(object value) => false;
        public override bool Contains(string value) => false;
        public override void CopyTo(Array array, int index) { }
        public override System.Collections.IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
        public override int IndexOf(object value) => -1;
        public override int IndexOf(string parameterName) => -1;
        public override void Insert(int index, object value) { }
        public override void Remove(object value) { }
        public override void RemoveAt(int index) { }
        public override void RemoveAt(string parameterName) { }
        protected override DbParameter GetParameter(int index) => throw new IndexOutOfRangeException();
        protected override void SetParameter(int index, DbParameter value) { }
        protected override DbParameter GetParameter(string parameterName) => throw new IndexOutOfRangeException();
        protected override void SetParameter(string parameterName, DbParameter value) { }
    }
}
