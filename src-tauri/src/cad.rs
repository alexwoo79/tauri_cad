use serde::{Deserialize, Serialize};

#[cfg(not(windows))]
use crate::app_error;
use crate::AppError;

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PolylineAreaRecord {
    pub layer: String,
    pub area: f64,
}

#[cfg(windows)]
mod windows_impl {
    use super::PolylineAreaRecord;
    use crate::{app_error, AppError};

    use windows::core::{IUnknown, Interface, BSTR, GUID, PCWSTR};
    use windows::Win32::Foundation::VARIANT_BOOL;
    use windows::Win32::Globalization::LOCALE_SYSTEM_DEFAULT;
    use windows::Win32::System::Com::{
        CLSIDFromProgID, CoCreateInstance, CoInitializeEx, CoUninitialize, IDispatch,
        CLSCTX_LOCAL_SERVER, COINIT_APARTMENTTHREADED, DISPATCH_FLAGS, DISPATCH_METHOD,
        DISPATCH_PROPERTYGET, DISPATCH_PROPERTYPUT, DISPPARAMS, EXCEPINFO,
    };
    use windows::Win32::System::Ole::{GetActiveObject, DISPID_PROPERTYPUT};
    use windows::Win32::System::Variant::{
        VariantClear, VARIANT, VT_BSTR, VT_DISPATCH, VT_I4, VT_R8,
    };

    struct ComApartment;

    impl ComApartment {
        fn new() -> Result<Self, AppError> {
            unsafe {
                CoInitializeEx(None, COINIT_APARTMENTTHREADED)
                    .ok()
                    .map_err(|e| app_error("COM_INIT_FAILED", e.to_string()))?;
            }
            Ok(Self)
        }
    }

    impl Drop for ComApartment {
        fn drop(&mut self) {
            unsafe {
                CoUninitialize();
            }
        }
    }

    fn to_wide(text: &str) -> Vec<u16> {
        text.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn get_dispid(obj: &IDispatch, name: &str) -> Result<i32, AppError> {
        let mut dispid = 0i32;
        let wide = to_wide(name);
        let name_ptr = [PCWSTR(wide.as_ptr())];
        unsafe {
            obj.GetIDsOfNames(
                &GUID::zeroed(),
                name_ptr.as_ptr(),
                1,
                LOCALE_SYSTEM_DEFAULT as u32,
                &mut dispid,
            )
            .map_err(|e| {
                app_error(
                    "COM_INVOKE_FAILED",
                    format!("GetIDsOfNames({name}) 失败: {e}"),
                )
            })?;
        }
        Ok(dispid)
    }

    fn invoke(
        obj: &IDispatch,
        name: &str,
        flags: DISPATCH_FLAGS,
        mut args: Vec<VARIANT>,
        named_dispid: Option<i32>,
    ) -> Result<VARIANT, AppError> {
        let dispid = get_dispid(obj, name)?;
        args.reverse();

        let mut named_id = named_dispid.unwrap_or_default();
        let mut disp_params = DISPPARAMS {
            rgvarg: if args.is_empty() {
                std::ptr::null_mut()
            } else {
                args.as_mut_ptr()
            },
            rgdispidNamedArgs: if named_dispid.is_some() {
                &mut named_id
            } else {
                std::ptr::null_mut()
            },
            cArgs: args.len() as u32,
            cNamedArgs: if named_dispid.is_some() { 1 } else { 0 },
        };

        let mut result = VARIANT::default();
        let mut excep = EXCEPINFO::default();
        let mut arg_err = 0u32;

        unsafe {
            obj.Invoke(
                dispid,
                &GUID::zeroed(),
                LOCALE_SYSTEM_DEFAULT as u32,
                flags,
                &mut disp_params,
                Some(&mut result),
                Some(&mut excep),
                Some(&mut arg_err),
            )
            .map_err(|e| {
                app_error(
                    "COM_INVOKE_FAILED",
                    format!("Invoke({name}) 失败: {e}; argIndex={arg_err}"),
                )
            })?;
        }

        Ok(result)
    }

    fn variant_i32(value: i32) -> VARIANT {
        let mut v = VARIANT::default();
        unsafe {
            let inner = &mut *v.Anonymous.Anonymous;
            inner.vt = VT_I4;
            inner.Anonymous.lVal = value;
        }
        v
    }

    fn variant_bool(value: bool) -> VARIANT {
        let mut v = VARIANT::default();
        unsafe {
            let inner = &mut *v.Anonymous.Anonymous;
            inner.vt = windows::Win32::System::Variant::VT_BOOL;
            inner.Anonymous.boolVal = if value {
                VARIANT_BOOL(-1)
            } else {
                VARIANT_BOOL(0)
            };
        }
        v
    }

    fn variant_bstr(value: &str) -> VARIANT {
        let mut v = VARIANT::default();
        unsafe {
            let inner = &mut *v.Anonymous.Anonymous;
            inner.vt = VT_BSTR;
            inner.Anonymous.bstrVal = std::mem::ManuallyDrop::new(BSTR::from(value));
        }
        v
    }

    fn variant_as_dispatch(v: &VARIANT) -> Result<IDispatch, AppError> {
        unsafe {
            let inner = &*v.Anonymous.Anonymous;
            if inner.vt != VT_DISPATCH {
                return Err(app_error("COM_TYPE_ERROR", "返回值不是 IDispatch"));
            }
            let some = (*inner.Anonymous.pdispVal)
                .as_ref()
                .ok_or_else(|| app_error("COM_NULL_DISPATCH", "空对象引用"))?;
            Ok(some.clone())
        }
    }

    fn variant_as_i32(v: &VARIANT) -> Result<i32, AppError> {
        unsafe {
            let inner = &*v.Anonymous.Anonymous;
            if inner.vt != VT_I4 {
                return Err(app_error("COM_TYPE_ERROR", "返回值不是 i32"));
            }
            Ok(inner.Anonymous.lVal)
        }
    }

    fn variant_as_f64(v: &VARIANT) -> Result<f64, AppError> {
        unsafe {
            let inner = &*v.Anonymous.Anonymous;
            if inner.vt == VT_R8 {
                return Ok(inner.Anonymous.dblVal);
            }
            Err(app_error("COM_TYPE_ERROR", "返回值不是 f64"))
        }
    }

    fn variant_as_string(v: &VARIANT) -> Result<String, AppError> {
        unsafe {
            let inner = &*v.Anonymous.Anonymous;
            if inner.vt != VT_BSTR {
                return Err(app_error("COM_TYPE_ERROR", "返回值不是字符串"));
            }
            let b = &*inner.Anonymous.bstrVal;
            Ok(b.to_string())
        }
    }

    fn get_prop(obj: &IDispatch, name: &str) -> Result<VARIANT, AppError> {
        invoke(obj, name, DISPATCH_PROPERTYGET, vec![], None)
    }

    fn call_method(obj: &IDispatch, name: &str, args: Vec<VARIANT>) -> Result<VARIANT, AppError> {
        invoke(obj, name, DISPATCH_METHOD, args, None)
    }

    fn set_prop(obj: &IDispatch, name: &str, value: VARIANT) -> Result<(), AppError> {
        let mut res = invoke(
            obj,
            name,
            DISPATCH_PROPERTYPUT,
            vec![value],
            Some(DISPID_PROPERTYPUT),
        )?;
        unsafe {
            let _ = VariantClear(&mut res);
        }
        Ok(())
    }

    fn clear_variant(mut v: VARIANT) {
        unsafe {
            let _ = VariantClear(&mut v);
        }
    }

    fn get_or_start_autocad() -> Result<IDispatch, AppError> {
        let progid = to_wide("AutoCAD.Application");
        let clsid = unsafe {
            CLSIDFromProgID(PCWSTR(progid.as_ptr()))
                .map_err(|e| app_error("AUTOCAD_NOT_REGISTERED", e.to_string()))?
        };

        unsafe {
            let mut unk: Option<IUnknown> = None;
            if GetActiveObject(&clsid, None, &mut unk).is_ok() {
                if let Some(u) = unk {
                    if let Ok(active) = u.cast::<IDispatch>() {
                        return Ok(active);
                    }
                }
            }

            CoCreateInstance::<_, IDispatch>(&clsid, None::<&IUnknown>, CLSCTX_LOCAL_SERVER)
                .map_err(|e| app_error("AUTOCAD_START_FAILED", e.to_string()))
        }
    }

    pub fn open_cad() -> Result<String, AppError> {
        let _apartment = ComApartment::new()?;
        let app = get_or_start_autocad()?;
        set_prop(&app, "Visible", variant_bool(true))?;
        Ok("AutoCAD 已开启并可见".to_string())
    }

    pub fn select_polyline_areas() -> Result<Vec<PolylineAreaRecord>, AppError> {
        let _apartment = ComApartment::new()?;
        let app = get_or_start_autocad()?;

        set_prop(&app, "Visible", variant_bool(true))?;

        let doc_v = get_prop(&app, "ActiveDocument")?;
        let doc = variant_as_dispatch(&doc_v)?;
        clear_variant(doc_v);

        let sets_v = get_prop(&doc, "SelectionSets")?;
        let sets = variant_as_dispatch(&sets_v)?;
        clear_variant(sets_v);

        let set_name = format!(
            "TauriSel_{}",
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_millis())
                .unwrap_or(0)
        );
        let _ = call_method(&sets, "Item", vec![variant_bstr(&set_name)]).and_then(|old| {
            let old_set = variant_as_dispatch(&old)?;
            clear_variant(old);
            let r = call_method(&old_set, "Delete", vec![]);
            r.map(clear_variant)
        });

        let sel_v = call_method(&sets, "Add", vec![variant_bstr(&set_name)])?;
        let sel = variant_as_dispatch(&sel_v)?;
        clear_variant(sel_v);

        let select_res = call_method(&sel, "SelectOnScreen", vec![])?;
        clear_variant(select_res);

        let count_v = get_prop(&sel, "Count")?;
        let count = variant_as_i32(&count_v)?;
        clear_variant(count_v);

        let mut records = Vec::new();
        for i in 0..count {
            let item_v = call_method(&sel, "Item", vec![variant_i32(i)])?;
            let item = variant_as_dispatch(&item_v)?;
            clear_variant(item_v);

            let object_name_v = get_prop(&item, "ObjectName")?;
            let object_name = variant_as_string(&object_name_v)?;
            clear_variant(object_name_v);

            if !object_name.contains("Polyline") {
                continue;
            }

            let layer_v = get_prop(&item, "Layer")?;
            let layer = variant_as_string(&layer_v)?;
            clear_variant(layer_v);

            let area_v = get_prop(&item, "Area")?;
            let area = variant_as_f64(&area_v)?;
            clear_variant(area_v);

            records.push(PolylineAreaRecord {
                layer,
                area: (area / 1_000_000.0 * 100.0).round() / 100.0,
            });
        }

        let del_res = call_method(&sel, "Delete", vec![])?;
        clear_variant(del_res);

        if records.is_empty() {
            return Err(app_error("SELECTION_EMPTY", "未选中可导出的 polyline"));
        }

        Ok(records)
    }
}

#[cfg(windows)]
pub fn open_cad() -> Result<String, AppError> {
    windows_impl::open_cad()
}

#[cfg(not(windows))]
pub fn open_cad() -> Result<String, AppError> {
    Err(app_error("UNSUPPORTED_OS", "仅支持 Windows"))
}

#[cfg(windows)]
pub fn select_polyline_areas() -> Result<Vec<PolylineAreaRecord>, AppError> {
    windows_impl::select_polyline_areas()
}

#[cfg(not(windows))]
pub fn select_polyline_areas() -> Result<Vec<PolylineAreaRecord>, AppError> {
    Err(app_error("UNSUPPORTED_OS", "仅支持 Windows"))
}
