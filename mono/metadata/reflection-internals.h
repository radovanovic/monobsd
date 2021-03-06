/* 
 * Copyright 2014 Xamarin Inc
 */
#ifndef __MONO_METADATA_REFLECTION_INTERNALS_H__
#define __MONO_METADATA_REFLECTION_INTERNALS_H__

#include <mono/metadata/reflection.h>
#include <mono/utils/mono-compiler.h>
#include <mono/utils/mono-error.h>

MonoType*
mono_reflection_get_type_checked (MonoImage *rootimage, MonoImage* image, MonoTypeNameParse *info, mono_bool ignorecase, mono_bool *type_resolve);

MonoObject*
mono_custom_attrs_get_attr_checked (MonoCustomAttrInfo *ainfo, MonoClass *attr_klass, MonoError *error);

MonoArray*
mono_reflection_get_custom_attrs_data_checked (MonoObject *obj, MonoError *error);

char*
mono_identifier_unescape_type_name_chars (char* identifier);

MonoImage *
mono_find_dynamic_image_owner (void *ptr);

#endif /* __MONO_METADATA_REFLECTION_INTERNALS_H__ */
