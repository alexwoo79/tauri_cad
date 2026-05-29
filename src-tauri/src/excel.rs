use serde::{Deserialize, Serialize};
#[cfg(windows)]
use std::process::Command;

use crate::{app_error, AppError};

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExcelSelectionData {
    pub rows: usize,
    pub cols: usize,
    pub values: Vec<Vec<String>>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExcelAnalysisResult {
    pub rows: usize,
    pub cols: usize,
    pub non_empty_cells: usize,
    pub numeric_count: usize,
    pub numeric_sum: f64,
    pub numeric_avg: f64,
}

#[cfg(windows)]
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct DirectReadResult {
    ok: bool,
    data: Option<ExcelSelectionData>,
    code: Option<String>,
    message: Option<String>,
}

fn parse_tsv(text: &str) -> ExcelSelectionData {
    let mut rows: Vec<Vec<String>> = text
        .lines()
        .map(|line| line.split('\t').map(|s| s.to_string()).collect::<Vec<_>>())
        .collect();

    while matches!(rows.last(), Some(last) if last.iter().all(|v| v.trim().is_empty())) {
        rows.pop();
    }

    let row_count = rows.len();
    let col_count = rows.iter().map(|r| r.len()).max().unwrap_or(0);

    ExcelSelectionData {
        rows: row_count,
        cols: col_count,
        values: rows,
    }
}

fn read_selected_range_from_clipboard() -> Result<ExcelSelectionData, AppError> {
    let mut clipboard = arboard::Clipboard::new()
        .map_err(|e| app_error("CLIPBOARD_ERROR", format!("无法访问剪贴板: {e}")))?;

    let text = clipboard
        .get_text()
        .map_err(|e| app_error("CLIPBOARD_ERROR", format!("剪贴板无可读表格文本: {e}")))?;

    let data = parse_tsv(&text);
    if data.rows == 0 || data.cols == 0 {
        return Err(app_error(
            "EXCEL_SELECTION_EMPTY",
            "剪贴板没有可解析的表格数据，请先在 Excel/WPS 中框选并复制区域",
        ));
    }

    Ok(data)
}

#[cfg(windows)]
fn read_selected_range_direct_windows() -> Result<ExcelSelectionData, AppError> {
    let script = r#"
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::UTF8

function Out-Result($ok, $code, $message, $data) {
    $obj = [ordered]@{
        ok = $ok
        code = $code
        message = $message
        data = $data
    }
    $obj | ConvertTo-Json -Compress -Depth 8
}

function Get-ActiveSpreadsheetApp {
    $progIds = @(
        'Excel.Application',
        'ket.Application',
        'KET.Application',
        'et.Application',
        'ET.Application'
    )

    foreach ($id in $progIds) {
        try {
            $obj = [Runtime.InteropServices.Marshal]::GetActiveObject($id)
            if ($null -ne $obj) {
                return $obj
            }
        } catch {
            # continue
        }
    }
    return $null
}

$excel = Get-ActiveSpreadsheetApp
if ($null -eq $excel) {
    Out-Result $false 'NO_ACTIVE_APP' '未检测到运行中的 Excel/WPS，请先打开表格并选中数据区域。' $null
    exit 0
}

$sel = $excel.Selection
if ($null -eq $sel) {
    Out-Result $false 'NO_SELECTION' '未获取到活动选区，请先在 Excel/WPS 中框选数据区域。' $null
    exit 0
}

$rows = [int]$sel.Rows.Count
$cols = [int]$sel.Columns.Count

if ($rows -le 0 -or $cols -le 0) {
    Out-Result $false 'EMPTY_SELECTION' '选区为空，请重新框选数据区域。' $null
    exit 0
}

$values = @()
for ($r = 1; $r -le $rows; $r++) {
    $row = @()
    for ($c = 1; $c -le $cols; $c++) {
        $cell = $sel.Item($r, $c)
        $v = $cell.Text
        if ($null -eq $v) { $row += '' } else { $row += [string]$v }
    }
    $values += ,$row
}

$selection = [ordered]@{
    rows = $rows
    cols = $cols
    values = $values
}

Out-Result $true $null $null $selection
"#;

    let output = Command::new("powershell")
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            script,
        ])
        .output()
        .map_err(|e| {
            app_error(
                "EXCEL_DIRECT_START_FAILED",
                format!("启动 PowerShell 失败: {e}"),
            )
        })?;

    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
        return Err(app_error(
            "EXCEL_DIRECT_READ_FAILED",
            if stderr.is_empty() {
                "Excel/WPS 直连读取失败，请确认应用已运行且已框选区域".to_string()
            } else {
                format!("Excel/WPS 直连读取失败: {stderr}")
            },
        ));
    }

    let mut stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
    if stdout.starts_with('\u{feff}') {
        stdout = stdout.trim_start_matches('\u{feff}').to_string();
    }
    if stdout.is_empty() {
        return Err(app_error(
            "EXCEL_DIRECT_EMPTY",
            "Excel/WPS 直连读取返回空结果，请确认已选中区域",
        ));
    }

    let direct: DirectReadResult = serde_json::from_str(&stdout).map_err(|e| {
        app_error(
            "EXCEL_DIRECT_PARSE_FAILED",
            format!("解析 Excel/WPS 直连结果失败: {e}; raw={stdout}"),
        )
    })?;

    if !direct.ok {
        return Err(app_error(
            direct.code.as_deref().unwrap_or("EXCEL_DIRECT_READ_FAILED"),
            direct
                .message
                .unwrap_or_else(|| "Excel/WPS 直连读取失败，请确认已选中区域".to_string()),
        ));
    }

    let data = direct.data.ok_or_else(|| {
        app_error(
            "EXCEL_DIRECT_EMPTY_DATA",
            "Excel/WPS 直连返回为空，请重新框选区域后重试",
        )
    })?;

    if data.rows == 0 || data.cols == 0 {
        return Err(app_error("EXCEL_SELECTION_EMPTY", "Excel/WPS 选区为空"));
    }

    Ok(data)
}

#[cfg(windows)]
pub fn read_selected_range() -> Result<ExcelSelectionData, AppError> {
    match read_selected_range_direct_windows() {
        Ok(data) => Ok(data),
        Err(direct_err) => match read_selected_range_from_clipboard() {
            Ok(data) => Ok(data),
            Err(clipboard_err) => Err(app_error(
                "EXCEL_READ_FAILED",
                format!(
                    "无法读取 Excel/WPS 选区。直连失败: {}; 剪贴板回退失败: {}",
                    direct_err.message, clipboard_err.message
                ),
            )),
        },
    }
}

#[cfg(not(windows))]
pub fn read_selected_range() -> Result<ExcelSelectionData, AppError> {
    read_selected_range_from_clipboard()
}

pub fn analyze_selected_range() -> Result<ExcelAnalysisResult, AppError> {
    let data = read_selected_range()?;

    if data.values.is_empty() {
        return Err(app_error(
            "EXCEL_SELECTION_EMPTY",
            "未读取到 Excel/WPS 选区数据",
        ));
    }

    let data_rows = data.values.len().saturating_sub(1);
    let values = if data.values.len() > 1 {
        &data.values[1..]
    } else {
        &[][..]
    };

    let mut non_empty_cells = 0usize;
    let mut numeric_count = 0usize;
    let mut numeric_sum = 0.0f64;

    for row in values {
        for value in row {
            let trimmed = value.trim();
            if trimmed.is_empty() {
                continue;
            }
            non_empty_cells += 1;
            if let Ok(v) = trimmed.replace(',', "").parse::<f64>() {
                numeric_count += 1;
                numeric_sum += v;
            }
        }
    }

    let numeric_avg = if numeric_count > 0 {
        numeric_sum / numeric_count as f64
    } else {
        0.0
    };

    Ok(ExcelAnalysisResult {
        rows: data_rows,
        cols: data.cols,
        non_empty_cells,
        numeric_count,
        numeric_sum: (numeric_sum * 10000.0).round() / 10000.0,
        numeric_avg: (numeric_avg * 10000.0).round() / 10000.0,
    })
}
