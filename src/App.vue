<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { invoke } from "@tauri-apps/api/core";

interface PolylineAreaRecord {
  layer: string;
  area: number;
}

interface ExportResult {
  records: PolylineAreaRecord[];
  writtenPath: string;
  count: number;
}

interface SelectResult {
  records: PolylineAreaRecord[];
  count: number;
}

interface ExcelSelectionData {
  rows: number;
  cols: number;
  values: string[][];
}

interface ExcelAnalysisResult {
  rows: number;
  cols: number;
  nonEmptyCells: number;
  numericCount: number;
  numericSum: number;
  numericAvg: number;
}

interface AppError {
  code?: string;
  message?: string;
}

const outputPath = ref("");
const overwrite = ref(false);
const loading = ref(false);
const status = ref("请按顺序点击：开启CAD → 选择图元 → 导出数据");
const records = ref<PolylineAreaRecord[]>([]);
const excelSelection = ref<ExcelSelectionData | null>(null);
const excelAnalysis = ref<ExcelAnalysisResult | null>(null);

const excelHeaders = computed(() => {
  const values = excelSelection.value?.values ?? [];
  if (values.length === 0) {
    return [] as string[];
  }

  const maxCols = Math.max(...values.map((row) => row.length), 0);
  const firstRow = values[0] ?? [];
  return Array.from({ length: maxCols }, (_, idx) => {
    const header = (firstRow[idx] ?? "").trim();
    return header || `列${idx + 1}`;
  });
});

const excelBodyRows = computed(() => {
  const values = excelSelection.value?.values ?? [];
  return values.length > 1 ? values.slice(1) : [];
});

function getExcelCell(row: string[], index: number): string {
  return row[index] ?? "";
}

async function initDefaultPath() {
  outputPath.value = await invoke<string>("default_output_path");
}

function normalizeError(err: unknown): AppError {
  if (!err) {
    return { code: "UNKNOWN", message: "未知错误" };
  }
  if (typeof err === "string") {
    return { code: "ERROR", message: err };
  }
  if (typeof err === "object") {
    return err as AppError;
  }
  return { code: "UNKNOWN", message: String(err) };
}

async function openCad() {
  loading.value = true;
  try {
    const msg = await invoke<string>("open_cad");
    status.value = msg;
  } catch (err) {
    const parsed = normalizeError(err);
    status.value = `开启失败${parsed.code ? ` [${parsed.code}]` : ""}：${parsed.message ?? "未知错误"}`;
  } finally {
    loading.value = false;
  }
}

async function selectEntities() {
  loading.value = true;
  status.value = "请在 AutoCAD 中选择 polyline，然后按 Enter 确认。";
  try {
    const result = await invoke<SelectResult>("select_entities");
    records.value = result.records;
    status.value = `选择完成，共 ${result.count} 条。请点击“导出数据”。`;
  } catch (err) {
    const parsed = normalizeError(err);
    status.value = `选择失败${parsed.code ? ` [${parsed.code}]` : ""}：${parsed.message ?? "未知错误"}`;
  } finally {
    loading.value = false;
  }
}

async function exportData() {
  if (!outputPath.value.trim()) {
    status.value = "请先输入导出路径。";
    return;
  }

  loading.value = true;
  status.value = "正在导出 CSV...";
  try {
    const result = await invoke<ExportResult>("export_selected_data", {
      outputPath: outputPath.value,
      overwrite: overwrite.value,
    });
    records.value = result.records;
    status.value = `导出成功，共 ${result.count} 条，文件：${result.writtenPath}`;
  } catch (err) {
    const parsed = normalizeError(err);
    status.value = `导出失败${parsed.code ? ` [${parsed.code}]` : ""}：${parsed.message ?? "未知错误"}`;
  } finally {
    loading.value = false;
  }
}

async function clearData() {
  loading.value = true;
  try {
    const msg = await invoke<string>("clear_selected_data");
    records.value = [];
    status.value = msg;
  } catch (err) {
    const parsed = normalizeError(err);
    status.value = `清除失败${parsed.code ? ` [${parsed.code}]` : ""}：${parsed.message ?? "未知错误"}`;
  } finally {
    loading.value = false;
  }
}

async function copyAreaData() {
  loading.value = true;
  try {
    const msg = await invoke<string>("copy_selected_data");
    status.value = msg;
  } catch (err) {
    const parsed = normalizeError(err);
    status.value = `复制失败${parsed.code ? ` [${parsed.code}]` : ""}：${parsed.message ?? "未知错误"}`;
  } finally {
    loading.value = false;
  }
}

async function copySummaryData() {
  loading.value = true;
  try {
    const msg = await invoke<string>("copy_summary_data");
    status.value = msg;
  } catch (err) {
    const parsed = normalizeError(err);
    status.value = `复制汇总失败${parsed.code ? ` [${parsed.code}]` : ""}：${parsed.message ?? "未知错误"}`;
  } finally {
    loading.value = false;
  }
}

async function readExcelSelection() {
  loading.value = true;
  status.value = "请先在 Excel 中选中目标区域，正在读取...";
  try {
    const data = await invoke<ExcelSelectionData>("read_excel_selection");
    excelSelection.value = data;
    const dataRows = Math.max(data.rows - 1, 0);
    status.value = `Excel 选区读取成功：${dataRows} 行数据 × ${data.cols} 列（首行为表头）`;
  } catch (err) {
    const parsed = normalizeError(err);
    status.value = `读取Excel失败${parsed.code ? ` [${parsed.code}]` : ""}：${parsed.message ?? "未知错误"}`;
  } finally {
    loading.value = false;
  }
}

async function analyzeExcelSelection() {
  loading.value = true;
  status.value = "正在分析 Excel 选区数据...";
  try {
    const result = await invoke<ExcelAnalysisResult>("analyze_excel_selection");
    excelAnalysis.value = result;
    status.value = "Excel 选区分析完成。";
  } catch (err) {
    const parsed = normalizeError(err);
    status.value = `分析Excel失败${parsed.code ? ` [${parsed.code}]` : ""}：${parsed.message ?? "未知错误"}`;
  } finally {
    loading.value = false;
  }
}

function clearExcelView() {
  excelSelection.value = null;
  excelAnalysis.value = null;
  status.value = "Excel 数据展示已清空";
}

onMounted(() => {
  initDefaultPath();
});
</script>

<template>
  <main class="container">
    <h1>AutoCAD 面积导出工具（Tauri 版）</h1>

    <section class="panel">
      <label for="outputPath">CSV 导出路径</label>
      <input
        id="outputPath"
        v-model="outputPath"
        type="text"
        placeholder="例如：C:\\Users\\xxx\\Desktop\\polyline_areas.csv"
      />

      <label class="checkbox-row">
        <input v-model="overwrite" type="checkbox" />
        覆盖同名文件
      </label>

      <div class="actions">
        <button :disabled="loading" @click="openCad">开启CAD</button>
        <button :disabled="loading" @click="selectEntities">选择图元</button>
        <button :disabled="loading" @click="exportData">
          {{ loading ? "处理中..." : "导出数据" }}
        </button>
      </div>
    </section>

    <p class="status">{{ status }}</p>

    <section class="panel table-panel">
      <div class="table-header">
        <h2>预览结果</h2>
        <div class="table-actions">
          <button class="copy-btn" :disabled="loading || records.length === 0" @click="copyAreaData">
            copy面积数据
          </button>
          <button
            class="copy-summary-btn"
            :disabled="loading || records.length === 0"
            @click="copySummaryData"
          >
            copy汇总数据
          </button>
          <button class="clear-btn" :disabled="loading || records.length === 0" @click="clearData">
            清除
          </button>
        </div>
      </div>
      <table>
        <thead>
          <tr>
            <th>图层</th>
            <th>面积（㎡）</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, idx) in records" :key="`${row.layer}-${idx}`">
            <td>{{ row.layer }}</td>
            <td>{{ row.area }}</td>
          </tr>
          <tr v-if="records.length === 0">
            <td colspan="2" class="empty">暂无数据</td>
          </tr>
        </tbody>
      </table>
    </section>

    <section class="panel table-panel">
      <div class="table-header">
        <h2>Excel 选区交互</h2>
        <div class="table-actions">
          <button class="excel-btn" :disabled="loading" @click="readExcelSelection">读取Excel选区</button>
          <button class="excel-analyze-btn" :disabled="loading" @click="analyzeExcelSelection">
            分析Excel选区
          </button>
          <button class="clear-btn" :disabled="loading" @click="clearExcelView">清空展示</button>
        </div>
      </div>

      <div v-if="excelAnalysis" class="analysis-grid">
        <div class="analysis-item">行数：{{ excelAnalysis.rows }}</div>
        <div class="analysis-item">列数：{{ excelAnalysis.cols }}</div>
        <div class="analysis-item">非空单元格：{{ excelAnalysis.nonEmptyCells }}</div>
        <div class="analysis-item">数值个数：{{ excelAnalysis.numericCount }}</div>
        <div class="analysis-item">数值总和：{{ excelAnalysis.numericSum }}</div>
        <div class="analysis-item">数值平均：{{ excelAnalysis.numericAvg }}</div>
      </div>

      <table v-if="excelSelection">
        <thead>
          <tr>
            <th v-for="(head, idx) in excelHeaders" :key="`excel-head-${idx}`">{{ head }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(row, rowIdx) in excelBodyRows" :key="`excel-row-${rowIdx}`">
            <td v-for="(_, colIdx) in excelHeaders" :key="`excel-cell-${rowIdx}-${colIdx}`">
              {{ getExcelCell(row, colIdx) }}
            </td>
          </tr>
          <tr v-if="excelBodyRows.length === 0">
            <td :colspan="Math.max(excelHeaders.length, 1)" class="empty">暂无数据行</td>
          </tr>
        </tbody>
      </table>

      <p v-else class="empty-text">暂无 Excel 选区数据</p>
    </section>
  </main>
</template>

<style>
:root {
  font-family: "Segoe UI", "PingFang SC", sans-serif;
  color: #1f2937;
  background: #f3f4f6;
}

* {
  box-sizing: border-box;
}

body {
  margin: 0;
}

.container {
  max-width: 900px;
  margin: 0 auto;
  padding: 24px;
}

h1 {
  margin: 0 0 16px;
  font-size: 24px;
}

.panel {
  background: #fff;
  border: 1px solid #e5e7eb;
  border-radius: 10px;
  padding: 16px;
  margin-bottom: 12px;
}

label {
  display: block;
  margin-bottom: 8px;
  font-weight: 600;
}

input[type="text"] {
  width: 100%;
  height: 40px;
  border: 1px solid #d1d5db;
  border-radius: 8px;
  padding: 0 12px;
}

.checkbox-row {
  margin-top: 12px;
  display: flex;
  align-items: center;
  gap: 8px;
}

.actions {
  margin-top: 14px;
  display: flex;
  gap: 10px;
}

button {
  height: 40px;
  padding: 0 16px;
  border: none;
  border-radius: 8px;
  background: #2563eb;
  color: #fff;
  cursor: pointer;
}

button:disabled {
  background: #93c5fd;
  cursor: not-allowed;
}

.status {
  margin: 8px 0 14px;
  padding: 10px 12px;
  border-radius: 8px;
  background: #eef2ff;
  color: #3730a3;
}

h2 {
  margin: 0 0 10px;
  font-size: 18px;
}

.table-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.table-actions {
  display: flex;
  gap: 8px;
}

.copy-btn {
  background: #0ea5e9;
}

.copy-btn:disabled {
  background: #7dd3fc;
}

.copy-summary-btn {
  background: #8b5cf6;
}

.copy-summary-btn:disabled {
  background: #c4b5fd;
}

.excel-btn {
  background: #059669;
}

.excel-btn:disabled {
  background: #6ee7b7;
}

.excel-analyze-btn {
  background: #d97706;
}

.excel-analyze-btn:disabled {
  background: #fcd34d;
}

.clear-btn {
  background: #ef4444;
}

.clear-btn:disabled {
  background: #fca5a5;
}

table {
  width: 100%;
  border-collapse: collapse;
}

th,
td {
  border-bottom: 1px solid #e5e7eb;
  text-align: left;
  padding: 8px;
}

.empty {
  text-align: center;
  color: #6b7280;
}

.empty-text {
  margin: 8px 0 0;
  color: #6b7280;
}

.analysis-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
  margin: 8px 0 12px;
}

.analysis-item {
  background: #f9fafb;
  border: 1px solid #e5e7eb;
  border-radius: 6px;
  padding: 8px 10px;
  font-size: 14px;
}
</style>