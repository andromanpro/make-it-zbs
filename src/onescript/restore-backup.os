// Explicit recovery only: source.zbsbackup -> a NEW UTF-8 file chosen by the user.
КоличествоАргументов = 2;
КодНеверныхАргументов = 2;
Если АргументыКоманднойСтроки.Количество() <> КоличествоАргументов Тогда
	Сообщить("Usage: restore-backup.os source.zbsbackup new-output.txt");
	ЗавершитьРаботу(КодНеверныхАргументов);
КонецЕсли;

Попытка
	Корень = ОбъединитьПути(ТекущийСценарий().Каталог, "..", "..");
	ПодключитьВнешнююКомпоненту(ОбъединитьПути(Корень, "lib", "net472", "ZBSWindowsComponent.dll"));
	Хранилище = Новый ХранилищеРезервныхКопий();
	Хранилище.ВосстановитьВФайл(АргументыКоманднойСтроки[0], АргументыКоманднойСтроки[1]);
	Сообщить("Recovered to the new file. It now contains UNENCRYPTED text.");
Исключение
	Сообщить("Recovery failed. Use the original Windows profile and a NEW output file.");
	ЗавершитьРаботу(1);
КонецПопытки;
