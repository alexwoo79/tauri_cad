mod cad;
mod excel;

use cad::PolylineAreaRecord;
use csv::WriterBuilder;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::Mutex;

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct AppError {
    code: String,
    message: String,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ExportResult {
    records: Vec<PolylineAreaRecord>,
    written_path: String,
    count: usize,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct SelectResult {
    records: Vec<PolylineAreaRecord>,
    count: usize,
}

#[derive(Default)]
struct AppState {
    selected_records: Mutex<Vec<PolylineAreaRecord>>,
}

pub(crate) fn app_error(code: &str, message: impl Into<String>) -> AppError {
    AppError {
        code: code.to_string(),
        message: message.into(),
    }
}

fn default_output_path_impl() -> String {
    let mut base = std::env::var("USERPROFILE")
        .map(PathBuf::from)
        .unwrap_or_else(|_| std::env::current_dir().unwrap_or_else(|_| PathBuf::from(".")));

    base.push("Desktop");
    if !base.exists() {
        base = std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."));
    }
    base.push("polyline_areas.csv");

    base.to_string_lossy().to_string()
}

#[tauri::command]
fn default_output_path() -> String {
    default_output_path_impl()
}

#[tauri::command]
fn open_cad() -> Result<String, AppError> {
    cad::open_cad()
}

#[tauri::command]
fn select_entities(state: tauri::State<AppState>) -> Result<SelectResult, AppError> {
    let records = cad::select_polyline_areas()?;
    let count = records.len();
    let mut guard = state
        .selected_records
        .lock()
        .map_err(|_| app_error("STATE_LOCK_FAILED", "状态锁获取失败"))?;
    *guard = records.clone();

    Ok(SelectResult { records, count })
}

#[tauri::command]
fn export_selected_data(
    output_path: String,
    overwrite: bool,
    state: tauri::State<AppState>,
) -> Result<ExportResult, AppError> {
    let csv_filepath = PathBuf::from(output_path);
    if csv_filepath.exists() && !overwrite {
        return Err(app_error(
            "CSV_EXISTS",
            format!("文件已存在：{}", csv_filepath.to_string_lossy()),
        ));
    }

    let records = {
        let guard = state
            .selected_records
            .lock()
            .map_err(|_| app_error("STATE_LOCK_FAILED", "状态锁获取失败"))?;
        guard.clone()
    };

    if records.is_empty() {
        return Err(app_error("NO_SELECTION", "请先点击“选择图元”"));
    }

    if let Some(parent) = csv_filepath.parent() {
        std::fs::create_dir_all(parent)
            .map_err(|e| app_error("IO_ERROR", format!("创建目录失败: {e}")))?;
    }

    let mut writer = WriterBuilder::new()
        .has_headers(true)
        .from_path(&csv_filepath)
        .map_err(|e| app_error("IO_ERROR", format!("打开 CSV 失败: {e}")))?;

    writer
        .write_record(["图层", "面积"])
        .map_err(|e| app_error("IO_ERROR", format!("写入表头失败: {e}")))?;

    for row in &records {
        writer
            .write_record([row.layer.as_str(), &row.area.to_string()])
            .map_err(|e| app_error("IO_ERROR", format!("写入数据失败: {e}")))?;
    }

    writer
        .flush()
        .map_err(|e| app_error("IO_ERROR", format!("写入文件失败: {e}")))?;

    Ok(ExportResult {
        records: records.clone(),
        written_path: csv_filepath.to_string_lossy().to_string(),
        count: records.len(),
    })
}

#[tauri::command]
fn clear_selected_data(state: tauri::State<AppState>) -> Result<String, AppError> {
    let mut guard = state
        .selected_records
        .lock()
        .map_err(|_| app_error("STATE_LOCK_FAILED", "状态锁获取失败"))?;
    guard.clear();
    Ok("预览数据已清除".to_string())
}

#[tauri::command]
fn copy_selected_data(state: tauri::State<AppState>) -> Result<String, AppError> {
    let records = {
        let guard = state
            .selected_records
            .lock()
            .map_err(|_| app_error("STATE_LOCK_FAILED", "状态锁获取失败"))?;
        guard.clone()
    };

    if records.is_empty() {
        return Err(app_error("NO_SELECTION", "请先点击“选择图元”"));
    }

    let mut lines = Vec::with_capacity(records.len() + 1);
    lines.push("图层\t面积".to_string());
    lines.extend(
        records
            .iter()
            .map(|row| format!("{}\t{}", row.layer, row.area)),
    );
    let text = lines.join("\r\n");

    let mut clipboard = arboard::Clipboard::new()
        .map_err(|e| app_error("CLIPBOARD_ERROR", format!("访问剪贴板失败: {e}")))?;
    clipboard
        .set_text(text)
        .map_err(|e| app_error("CLIPBOARD_ERROR", format!("写入剪贴板失败: {e}")))?;

    Ok(format!(
        "已复制 {} 条面积数据到剪贴板，可直接粘贴到 Excel",
        records.len()
    ))
}

#[tauri::command]
fn copy_summary_data(state: tauri::State<AppState>) -> Result<String, AppError> {
    let records = {
        let guard = state
            .selected_records
            .lock()
            .map_err(|_| app_error("STATE_LOCK_FAILED", "状态锁获取失败"))?;
        guard.clone()
    };

    if records.is_empty() {
        return Err(app_error("NO_SELECTION", "请先点击“选择图元”"));
    }

    let mut summary: BTreeMap<String, f64> = BTreeMap::new();
    for row in &records {
        let entry = summary.entry(row.layer.clone()).or_insert(0.0);
        *entry += row.area;
    }

    let mut lines = Vec::with_capacity(summary.len() + 2);
    lines.push("图层\t汇总面积".to_string());

    let mut total = 0.0;
    for (layer, area) in &summary {
        let rounded = (area * 100.0).round() / 100.0;
        total += rounded;
        lines.push(format!("{}\t{}", layer, rounded));
    }
    lines.push(format!("总计\t{}", (total * 100.0).round() / 100.0));

    let text = lines.join("\r\n");
    let mut clipboard = arboard::Clipboard::new()
        .map_err(|e| app_error("CLIPBOARD_ERROR", format!("访问剪贴板失败: {e}")))?;
    clipboard
        .set_text(text)
        .map_err(|e| app_error("CLIPBOARD_ERROR", format!("写入剪贴板失败: {e}")))?;

    Ok(format!(
        "已复制 {} 个图层汇总数据到剪贴板，可直接粘贴到 Excel",
        summary.len()
    ))
}

#[tauri::command]
fn read_excel_selection() -> Result<excel::ExcelSelectionData, AppError> {
    excel::read_selected_range()
}

#[tauri::command]
fn analyze_excel_selection() -> Result<excel::ExcelAnalysisResult, AppError> {
    excel::analyze_selected_range()
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(AppState::default())
        .plugin(tauri_plugin_opener::init())
        .invoke_handler(tauri::generate_handler![
            default_output_path,
            open_cad,
            select_entities,
            export_selected_data,
            clear_selected_data,
            copy_selected_data,
            copy_summary_data,
            read_excel_selection,
            analyze_excel_selection
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
