using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace Traymetry
{
    /// <summary>
    /// One language the UI can be shown in: the tag stored in the registry, the
    /// name as its own speakers write it, and its strings.
    /// </summary>
    internal sealed class LanguagePack
    {
        public LanguagePack(string code, string nativeName, Dictionary<string, string> strings)
        {
            Code = code;
            NativeName = nativeName;
            Strings = strings ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        public string Code { get; private set; }
        public string NativeName { get; private set; }
        public Dictionary<string, string> Strings { get; private set; }
    }

    /// <summary>
    /// The string table for the whole UI, one pack per language code.  Keys are
    /// stable identifiers, so a missing translation shows up as the key itself
    /// rather than silently falling back and looking correct.
    ///
    /// Adding a language is one <see cref="Install"/> call with its own
    /// dictionary - not a column added to every row of a table that is already
    /// several hundred rows long.  Keys a pack does not carry fall back to
    /// English and then to Russian, so a half-finished translation still shows
    /// the rest of the window in a language someone can read.
    /// </summary>
    internal static class Loc
    {
        private static readonly List<LanguagePack> Packs = BuildPacks();
        private static string _code = DetectStartupLanguage();

        /// <summary>
        /// The language in use, as its registry tag.  Changing this only swaps
        /// which pack is read.  Repainting is the caller's job, because the one
        /// caller that needs it already knows the exact set of captions to
        /// replay and can do it without recreating any control.
        /// </summary>
        public static string Code
        {
            get { return _code; }
            set { _code = Parse(value, _code); }
        }

        /// <summary>Every installed language, in menu order.</summary>
        public static IEnumerable<LanguagePack> Languages
        {
            get { return Packs.ToArray(); }
        }

        /// <summary>
        /// Adds a language before the first string is read.  The dictionary is
        /// taken as given: a pack with ten keys in it is a perfectly valid pack.
        /// </summary>
        public static void Install(string code, string nativeName,
            Dictionary<string, string> strings)
        {
            if (String.IsNullOrEmpty(code) || strings == null)
                return;
            for (int index = 0; index < Packs.Count; index++)
            {
                if (!String.Equals(Packs[index].Code, code, StringComparison.OrdinalIgnoreCase))
                    continue;
                Packs[index] = new LanguagePack(code, nativeName, strings);
                return;
            }
            Packs.Add(new LanguagePack(code, nativeName, strings));
        }

        /// <summary>The code after this one, wrapping - what the header button does.</summary>
        public static string NextCode()
        {
            for (int index = 0; index < Packs.Count; index++)
            {
                if (!String.Equals(Packs[index].Code, _code, StringComparison.OrdinalIgnoreCase))
                    continue;
                return Packs[(index + 1) % Packs.Count].Code;
            }
            return Packs.Count > 0 ? Packs[0].Code : _code;
        }

        /// <summary>An installed language code, or the fallback.</summary>
        public static string Parse(string value, string fallback)
        {
            if (!String.IsNullOrEmpty(value))
                foreach (LanguagePack pack in Packs)
                    if (String.Equals(pack.Code, value, StringComparison.OrdinalIgnoreCase))
                        return pack.Code;
            return fallback;
        }

        /// <summary>
        /// What the header button comes back to from English: the system
        /// language if it has a pack, and Russian otherwise - Russian being the
        /// only other language in the box, and a button that toggles English
        /// with English being no button at all.
        /// </summary>
        public static string PreferredDefault()
        {
            string system = DetectSystemLanguage();
            return String.Equals(system, "en", StringComparison.OrdinalIgnoreCase)
                ? "ru"
                : system;
        }

        /// <summary>
        /// The stored choice is read here rather than only by the settings load,
        /// because the update helper and the elevated setup pass run as separate
        /// processes that never open the settings yet still show message boxes.
        /// </summary>
        private static string DetectStartupLanguage()
        {
            string system = DetectSystemLanguage();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Traymetry"))
                {
                    if (key == null)
                        return system;
                    return Parse(Convert.ToString(key.GetValue("Language", String.Empty),
                        CultureInfo.InvariantCulture), system);
                }
            }
            catch (Exception)
            {
                return system;
            }
        }

        /// <summary>
        /// The language a machine with nothing stored starts in: its own, if
        /// that language is in the box, and English otherwise.  English is the
        /// fallback rather than Russian because an unrecognised system is far
        /// more likely to belong to someone who reads English than to someone
        /// who reads Russian, and a UI in a script you cannot read hides even
        /// the button that would change it.
        /// </summary>
        private static string DetectSystemLanguage()
        {
            try
            {
                return Parse(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "en");
            }
            catch (Exception)
            {
                return "en";
            }
        }

        public static string T(string key)
        {
            if (key == null)
                return String.Empty;
            string text = Lookup(_code, key);
            if (text == null && !String.Equals(_code, "en", StringComparison.OrdinalIgnoreCase))
                text = Lookup("en", key);
            if (text == null && !String.Equals(_code, "ru", StringComparison.OrdinalIgnoreCase))
                text = Lookup("ru", key);
            return text ?? key;
        }

        public static string T(string key, params object[] arguments)
        {
            return String.Format(CultureInfo.CurrentCulture, T(key), arguments);
        }

        private static string Lookup(string code, string key)
        {
            foreach (LanguagePack pack in Packs)
            {
                if (!String.Equals(pack.Code, code, StringComparison.OrdinalIgnoreCase))
                    continue;
                string text;
                if (pack.Strings.TryGetValue(key, out text) && !String.IsNullOrEmpty(text))
                    return text;
                return null;
            }
            return null;
        }

        /// <summary>
        /// The two languages that ship in the box.  They are written as one
        /// two-column table because they were translated together, line by
        /// line; the columns are split into packs here, and nothing outside
        /// this method knows the table has columns at all.
        /// </summary>
        private static List<LanguagePack> BuildPacks()
        {
            Dictionary<string, string[]> table = BuildTable();
            List<LanguagePack> packs = new List<LanguagePack>();
            packs.Add(new LanguagePack("ru", "Русский", Column(table, 0)));
            packs.Add(new LanguagePack("en", "English", Column(table, 1)));
            return packs;
        }

        private static Dictionary<string, string> Column(Dictionary<string, string[]> table,
            int index)
        {
            Dictionary<string, string> strings =
                new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string[]> entry in table)
            {
                string text = entry.Value != null && index < entry.Value.Length
                    ? entry.Value[index]
                    : null;
                if (!String.IsNullOrEmpty(text))
                    strings[entry.Key] = text;
            }
            return strings;
        }

        private static Dictionary<string, string[]> BuildTable()
        {
            Dictionary<string, string[]> table =
                new Dictionary<string, string[]>(StringComparer.Ordinal);

            // -- menu ------------------------------------------------------
            table["menu.cards"] = new[] { "Карточки", "Cards" };
            table["menu.exit"] = new[] { "Выход", "Exit" };
            table["menu.opacity"] = new[] { "Прозрачность", "Opacity" };
            table["menu.background"] = new[] { "Без фона", "No background" };
            table["menu.streamHidden"] = new[] { "Не попадать в запись экрана", "Hide from screen capture" };
            table["menu.topMost"] = new[] { "Поверх всех окон", "Always on top" };
            table["menu.startup"] = new[] { "Запускать вместе с Windows", "Start with Windows" };
            table["menu.support"] = new[] { "Поддержать Traymetry ♥", "Support Traymetry ♥" };
            table["pawnio.tooLarge"] = new[]
            {
                "Скачанный установщик PawnIO неправдоподобно велик — установка отменена.",
                "The downloaded PawnIO installer is implausibly large - the install was cancelled."
            };
            table["menu.checkUpdates"] = new[] { "Проверить обновления…", "Check for updates…" };
            table["menu.repairSensors"] = new[] { "Проверить и починить датчики…", "Check and repair sensors…" };
            table["menu.removeService"] = new[] { "Удалить системный сервис датчиков…", "Remove the sensor system service…" };
            // Three states of one thing, so they live in one submenu and read as
            // a choice rather than as three switches that can contradict.
            table["menu.header"] = new[] { "Верхняя панель", "Top bar" };
            table["menu.header.auto"] = new[]
            {
                "Автоматически: прячется без курсора",
                "Automatic: hides without the pointer"
            };
            table["menu.header.always"] = new[] { "Всегда показывать", "Always show" };
            table["menu.header.never"] = new[] { "Всегда скрывать", "Always hide" };
            // The four fixed positions are called cards everywhere the user can
            // see them: the menu they live in is called Cards too, and one name
            // for one thing is one thing less to work out.
            table["menu.slotPrefix"] = new[] { "Карточка ", "Card " };
            table["menu.cycleCards"] = new[] { "Листать карточки", "Cycle cards" };
            // The languages themselves are named by their own packs, so the menu
            // reads "Русский" in an English window rather than "Russian".
            table["menu.language"] = new[] { "Язык", "Language" };

            // -- colours ---------------------------------------------------
            table["menu.valueColour"] = new[] { "Цвет значений", "Value colour" };
            table["menu.color.pick"] = new[] { "Выбрать цвет…", "Pick a colour…" };
            table["menu.color.reset"] = new[] { "Вернуть стандартный", "Restore the default" };
            table["menu.color.resetAll"] = new[] { "Сбросить все цвета", "Reset every colour" };
            table["menu.color.myPaletteEmpty"] = new[] { "Палитра: Моя (пока пусто)", "Palette: Mine (not set yet)" };
            table["menu.color.myPaletteCount"] = new[] { "Палитра: Моя ({0})", "Palette: Mine ({0})" };

            // -- graphs ----------------------------------------------------
            table["menu.graphs"] = new[] { "Графики", "Graphs" };
            table["menu.graph.left"] = new[] { "Левый график", "Left graph" };
            table["menu.graph.right"] = new[] { "Правый график", "Right graph" };

            // -- presets ---------------------------------------------------
            table["preset.system"] = new[] { "Пресет: Система", "Preset: System" };
            table["preset.gaming"] = new[] { "Пресет: Игры", "Preset: Gaming" };
            table["preset.custom"] = new[] { "Пресет: Мой", "Preset: Mine" };
            table["preset.custom.prefix"] = new[] { "Пресет: Мой (", "Preset: Mine (" };
            table["preset.custom.empty"] = new[] { "Пресет: Мой (пока пусто)", "Preset: Mine (not set yet)" };

            // -- card names ------------------------------------------------
            table["card.memory"] = new[] { "Память", "Memory" };
            table["card.network"] = new[] { "Сеть", "Network" };
            table["card.storage"] = new[] { "Хранилище", "Storage" };
            table["card.fans"] = new[] { "Вентиляторы", "Fans" };
            table["card.metric"] = new[] { "Показатель", "Metric" };

            // -- captions --------------------------------------------------
            table["caption.temperature"] = new[] { "ТЕМПЕРАТУРА", "TEMPERATURE" };
            table["caption.load"] = new[] { "НАГРУЗКА", "LOAD" };
            table["caption.clock"] = new[] { "ЧАСТОТА", "CLOCK" };
            table["caption.power"] = new[] { "МОЩНОСТЬ", "POWER" };
            table["caption.memory"] = new[] { "ПАМЯТЬ", "MEMORY" };
            table["caption.gpuMemory"] = new[] { "ПАМЯТЬ GPU", "GPU MEMORY" };
            table["caption.network"] = new[] { "СЕТЬ", "NETWORK" };
            table["caption.storage"] = new[] { "ХРАНИЛИЩЕ", "STORAGE" };
            table["caption.storagePadded"] = new[] { "ХРАНИЛИЩЕ   ", "STORAGE   " };
            table["caption.fan"] = new[] { "ВЕНТИЛЯТОР", "FAN" };
            table["caption.fans"] = new[] { "ВЕНТИЛЯТОРЫ", "FANS" };
            table["caption.metric"] = new[] { "ПОКАЗАТЕЛЬ", "METRIC" };
            table["caption.download"] = new[] { "ЗАГРУЗКА", "DOWNLOAD" };
            table["caption.upload"] = new[] { "ОТДАЧА", "UPLOAD" };
            table["caption.used"] = new[] { "ЗАНЯТО", "USED" };
            table["caption.usedPadded"] = new[] { "ЗАНЯТО  ", "USED  " };
            table["caption.usedLong"] = new[] { "ИСПОЛЬЗОВАНО", "IN USE" };
            table["caption.usagePadded"] = new[] { "ИСПОЛЬЗОВАНИЕ  ", "USAGE  " };
            table["caption.allDrives"] = new[] { "ВСЕ ДИСКИ", "ALL DRIVES" };
            table["caption.drive"] = new[] { "ДИСК", "DRIVE" };
            table["caption.frameTime"] = new[] { "ВРЕМЯ КАДРА", "FRAME TIME" };
            table["caption.source"] = new[] { "ИСТОЧНИК", "SOURCE" };
            table["caption.opacityPadded"] = new[] { "ПРОЗРАЧНОСТЬ  ", "OPACITY  " };
            table["caption.opacitySample"] = new[] { "ПРОЗРАЧНОСТЬ  90%", "OPACITY  90%" };
            table["caption.noBackground"] = new[] { "БЕЗ ФОНА", "NO BACKGROUND" };

            // -- states ----------------------------------------------------
            table["state.off"] = new[] { "ВЫКЛЮЧЕНО", "OFF" };
            table["state.starting"] = new[] { "ЗАПУСК", "STARTING" };
            table["state.waiting"] = new[] { "ОЖИДАНИЕ", "WAITING" };
            table["state.waitingShort"] = new[] { "Ожидание данных…", "Waiting for data…" };
            table["state.waitingSensors"] = new[] { "Ожидание данных датчиков…", "Waiting for sensor data…" };
            table["state.unavailable"] = new[] { "НЕДОСТУПНО", "UNAVAILABLE" };
            table["state.noData"] = new[] { "НЕТ ДАННЫХ", "NO DATA" };
            table["state.noFrames"] = new[] { "НЕТ КАДРОВ", "NO FRAMES" };
            table["state.collecting"] = new[] { "СБОР ДАННЫХ", "COLLECTING" };
            table["gpu.notDetected"] = new[] { "GPU не обнаружен", "GPU not detected" };
            table["fps.idleSuffix"] = new[] { " · простой", " · idle" };
            table["fps.desktop"] = new[] { "рабочий стол", "desktop" };

            // -- history panel ---------------------------------------------
            table["history.titlePrefix"] = new[] { "ИСТОРИЯ ", "HISTORY " };
            table["history.minAvgMax"] = new[] { "МИН · СРЕД · МАКС", "MIN · AVG · MAX" };
            table["history.collecting"] = new[] { "НАКАПЛИВАЕМ ИСТОРИЮ…", "COLLECTING HISTORY…" };
            table["history.tempShort"] = new[] { "ТЕМП.", "TEMP" };
            table["history.tempShortAxis"] = new[] { "ТЕМП., °C", "TEMP, °C" };
            table["history.tempAxis"] = new[] { "ТЕМПЕРАТУРА, °C", "TEMPERATURE, °C" };
            table["history.loadShortAxis"] = new[] { "НАГР., %", "LOAD, %" };
            table["history.loadAxis"] = new[] { "НАГРУЗКА, %", "LOAD, %" };
            table["history.speedAxis"] = new[] { "СКОРОСТЬ, МБ/С", "SPEED, MB/S" };
            table["history.speedShortAxis"] = new[] { "СКОР., МБ/С", "SPD, MB/S" };
            table["history.rpmAxis"] = new[] { "ОБОРОТЫ, RPM", "SPEED, RPM" };
            table["history.rpmShortAxis"] = new[] { "ОБ., RPM", "RPM" };
            table["history.fpsAxis"] = new[] { "КАДРЫ, FPS", "FRAMES, FPS" };
            table["history.fpsShortAxis"] = new[] { "FPS", "FPS" };
            table["history.usedAxis"] = new[] { "ЗАНЯТО, %", "USED, %" };

            // -- tooltips --------------------------------------------------
            table["tip.title"] = new[]
            {
                "Двойной клик — развернуть окно на максимум / свернуть обратно",
                "Double click — open the window up to its largest view / put it back"
            };
            table["tip.opacity"] = new[]
            {
                "Клик / средняя кнопка — показать или скрыть\nКолесо мыши — изменить прозрачность",
                "Click / middle button — show or hide\nMouse wheel — change opacity"
            };
            table["tip.background.restore"] = new[] { "Вернуть фон", "Restore the background" };
            table["tip.background.remove"] = new[] { "Убрать фон", "Remove the background" };
            table["tip.pin.on"] = new[]
            {
                "Положение закреплено. Нажмите пин или {0}, чтобы разблокировать",
                "Position locked. Press the pin or {0} to unlock"
            };
            table["tip.pin.off"] = new[]
            {
                "Закрепить положение и отключить клики по утилите ({0})",
                "Lock the position and make the widget click-through ({0})"
            };
            table["tip.expand"] = new[] { "Скрыть в область уведомлений", "Hide to the notification area" };
            table["tip.cycle.default"] = new[]
            {
                "Листать или переставлять карточки: CPU → GPU → Сеть",
                "Page or rearrange the cards: CPU → GPU → Network"
            };
            table["tip.cycle.prefix"] = new[] { "Листать карточки: ", "Cycle cards: " };
            table["tip.cycle.all"] = new[]
            {
                "Сдвинуть порядок карточек: ",
                "Shift the card order: "
            };
            table["tip.streamHidden"] = new[]
            {
                "Окно остаётся видимым на мониторе, но пропадает из OBS, Discord и скриншотов",
                "The window stays visible on the monitor but disappears from OBS, Discord and screenshots"
            };

            // -- accessible names ------------------------------------------
            table["access.opacity"] = new[] { "Настроить прозрачность", "Adjust opacity" };
            table["access.background"] = new[] { "Показать или убрать фон", "Show or hide the background" };
            table["access.backgroundToggle"] = new[] { "Включить или отключить фон", "Turn the background on or off" };
            table["access.cycle"] = new[] { "Следующий показатель", "Next metric" };
            table["access.pin"] = new[] { "Закрепить положение", "Lock the position" };
            table["access.superStats"] = new[] { "Дополнительная статистика", "Extra statistics" };

            // -- service and support dialogs -------------------------------
            table["tray.waiting"] = new[] { "Traymetry — ожидание данных", "Traymetry — waiting for data" };
            table["service.remove.title"] = new[] { "Traymetry — удаление сервиса", "Traymetry — removing the service" };
            table["service.remove.body"] = new[]
            {
                "Traymetry остановит и удалит свой системный сервис датчиков. Сам файл Traymetry и драйвер PawnIO удалены не будут.\r\n\r\n",
                "Traymetry will stop and remove its own sensor system service. The Traymetry executable and the PawnIO driver are left in place.\r\n\r\n"
            };
            table["service.removed"] = new[]
            {
                "Системный сервис Traymetry удалён. Без него часть показателей CPU может быть недоступна.",
                "The Traymetry system service has been removed. Without it some CPU readings may be unavailable."
            };
            table["service.removeFailed"] = new[]
            {
                "Не удалось удалить системный сервис Traymetry.",
                "Could not remove the Traymetry system service."
            };
            table["service.setupFailed"] = new[]
            {
                "Не удалось настроить сервис датчиков Traymetry.",
                "Could not set up the Traymetry sensor service."
            };
            table["service.ready"] = new[]
            {
                "Сервис датчиков работает. Показания появятся в Traymetry через несколько секунд.",
                "The sensor service is running. Readings will appear in Traymetry within a few seconds."
            };
            table["support.openFailed"] = new[]
            {
                "Не удалось открыть страницу поддержки.",
                "Could not open the support page."
            };
            table["common.continueQuestion"] = new[] { "Продолжить?", "Continue?" };
            // The wording leads with what the person gets, then with what it
            // costs, and says out loud that saying no still leaves a working
            // widget.  A first screen that opens with "will install a driver"
            // is read as a demand, and the honest answer to a demand from an
            // unknown program is no.
            table["service.consent.title"] = new[]
            {
                "Traymetry — датчики процессора",
                "Traymetry — CPU sensors"
            };
            table["service.consent.body"] = new[]
            {
                "Traymetry может показывать температуру, частоту и мощность процессора — те же показания, что дают HWiNFO и AIDA64.\r\n\r\n" +
                "Windows отдаёт их только через драйвер, поэтому нужна разовая настройка: подписанный драйвер PawnIO (официальная сборка, скачивается со страницы релиза) и небольшой сервис Traymetry. Дальше всё работает само, при следующих запусках ничего не спрашивается.\r\n\r\n" +
                "Сервис отдаёт окну только готовые числа. Прямой доступ к железу остаётся закрыт для обычных программ — включая само окно Traymetry.\r\n\r\n" +
                "Сейчас Windows один раз спросит права администратора. Если отказаться, виджет продолжит работать — без температур процессора.\r\n\r\n" +
                "Продолжить?",
                "Traymetry can show CPU temperature, clock and power — the same readings HWiNFO and AIDA64 give you.\r\n\r\n" +
                "Windows only exposes them through a driver, so this is a one-time setup: the signed PawnIO driver (the official build, downloaded from its release page) and a small Traymetry service. After that it runs on its own and later starts ask nothing.\r\n\r\n" +
                "The service hands the window finished numbers only. Direct hardware access stays closed to ordinary programs — including Traymetry's own window.\r\n\r\n" +
                "Windows will now ask for administrator permission once. Decline and the widget keeps running — without CPU temperatures.\r\n\r\n" +
                "Continue?"
            };
            table["service.setupStartFailed"] = new[]
            {
                "Не удалось запустить настройку датчиков.",
                "Could not start the sensor setup."
            };
            table["service.setupExitCode"] = new[]
            {
                "Настройка датчиков завершилась с кодом {0}.",
                "The sensor setup finished with exit code {0}."
            };
            table["service.startTimeout"] = new[]
            {
                "Сервис датчиков не запустился вовремя.",
                "The sensor service did not start in time."
            };
            table["service.setupFailedDetails"] = new[]
            {
                "Не удалось настроить сервис датчиков. Traymetry продолжит работу, но часть показателей CPU может быть недоступна.\r\n\r\n",
                "Could not set up the sensor service. Traymetry keeps running, but some CPU readings may be unavailable.\r\n\r\n"
            };
            table["service.description"] = new[]
            {
                "Безопасно предоставляет приложению Traymetry готовые показания датчиков оборудования.",
                "Safely provides the Traymetry application with finished hardware sensor readings."
            };
            table["service.dirIsReparse"] = new[]
            {
                "Системный каталог Traymetry является точкой повторной обработки.",
                "The Traymetry system directory is a reparse point."
            };
            table["service.tempFailed"] = new[]
            {
                "Не удалось создать защищённый временный каталог Traymetry.",
                "Could not create the protected Traymetry temporary directory."
            };
            table["service.dirMustNotReparse"] = new[]
            {
                "Системный каталог Traymetry не может быть точкой повторной обработки.",
                "The Traymetry system directory must not be a reparse point."
            };
            table["service.dirReplaced"] = new[]
            {
                "Системный каталог Traymetry был подменён во время настройки.",
                "The Traymetry system directory was replaced during setup."
            };
            table["service.dirCreatedReparse"] = new[]
            {
                "Созданный системный каталог Traymetry оказался точкой повторной обработки.",
                "The Traymetry system directory that was created turned out to be a reparse point."
            };

            // -- PawnIO bootstrap ------------------------------------------
            table["pawnio.launchFailed"] = new[]
            {
                "Не удалось запустить установщик PawnIO.",
                "Could not start the PawnIO installer."
            };
            table["pawnio.hashMismatch"] = new[]
            {
                "Контрольная сумма официального установщика PawnIO не совпадает.",
                "The checksum of the official PawnIO installer does not match."
            };
            table["pawnio.signerMismatch"] = new[]
            {
                "Цифровая подпись установщика PawnIO не соответствует ожидаемому издателю.",
                "The PawnIO installer signature does not match the expected publisher."
            };

            // -- application start -----------------------------------------
            table["app.alreadyRunning"] = new[]
            {
                "Traymetry уже запущен. Закройте работающий экземпляр через меню «Выход», затем запустите приложение снова.",
                "Traymetry is already running. Close the running instance from the Exit menu, then start the application again."
            };
            table["app.updateRolledBack"] = new[]
            {
                "Обновление не удалось применить. Запущена прежняя версия Traymetry; подробности записаны в журнал обновления.",
                "The update could not be applied. The previous version of Traymetry is running; the details were written to the update log."
            };
            table["app.updateTitle"] = new[] { "Traymetry — обновление", "Traymetry — update" };

            // -- updates ---------------------------------------------------
            table["update.title"] = new[] { "Обновление Traymetry", "Traymetry update" };
            table["update.inProgress"] = new[]
            {
                "Проверка обновлений уже выполняется.",
                "An update check is already running."
            };
            table["update.upToDate"] = new[]
            {
                "У вас установлена последняя версия Traymetry.",
                "You already have the latest version of Traymetry."
            };
            table["update.available"] = new[]
            {
                "Доступна новая версия Traymetry {0}.\r\n\r\n" +
                "Скачать обновление, проверить цифровую подпись и перезапустить приложение?",
                "A new version of Traymetry {0} is available.\r\n\r\n" +
                "Download the update, verify its digital signature and restart the application?"
            };
            table["update.checkFailed"] = new[]
            {
                "Не удалось проверить обновления.\r\n\r\n",
                "Could not check for updates.\r\n\r\n"
            };
            table["update.installFailed"] = new[]
            {
                "Не удалось установить обновление. Текущая версия не изменена.\r\n\r\n",
                "Could not install the update. The current version is unchanged.\r\n\r\n"
            };
            table["update.sizeMismatch"] = new[]
            {
                "Размер загруженного обновления не совпадает с подписанным манифестом.",
                "The size of the downloaded update does not match the signed manifest."
            };
            table["update.hashMismatch"] = new[]
            {
                "SHA-256 загруженного файла не совпадает с GitHub Releases.",
                "The SHA-256 of the downloaded file does not match GitHub Releases."
            };
            table["update.tooLarge"] = new[]
            {
                "Загрузка превысила размер из подписанного манифеста.",
                "The download exceeded the size stated in the signed manifest."
            };
            table["update.manifestInvalid"] = new[]
            {
                "Некорректные данные манифеста обновления.",
                "Invalid update manifest data."
            };
            table["update.manifestSize"] = new[]
            {
                "Подписанный манифест обновления имеет недопустимый размер.",
                "The signed update manifest has an unacceptable size."
            };
            table["update.signatureFormat"] = new[]
            {
                "Подпись обновления имеет неверный формат.",
                "The update signature is malformed."
            };
            table["update.signatureInvalid"] = new[]
            {
                "RSA-подпись обновления Traymetry недействительна.",
                "The RSA signature of the Traymetry update is invalid."
            };
            table["update.manifestUtf8"] = new[]
            {
                "Манифест обновления не является корректным UTF-8.",
                "The update manifest is not valid UTF-8."
            };
            table["update.manifestShape"] = new[]
            {
                "Структура манифеста обновления не поддерживается.",
                "The structure of the update manifest is not supported."
            };
            table["update.manifestCorrupt"] = new[]
            {
                "Манифест обновления повреждён.",
                "The update manifest is damaged."
            };
            table["update.manifestDuplicate"] = new[]
            {
                "Манифест обновления содержит повторяющееся поле.",
                "The update manifest contains a duplicate field."
            };
            table["update.manifestValues"] = new[]
            {
                "Манифест обновления содержит неверные значения.",
                "The update manifest contains invalid values."
            };
            table["update.fileMissing"] = new[]
            {
                "Файл обновления или текущий EXE не найден.",
                "The update file or the current executable was not found."
            };
            table["update.elevated"] = new[]
            {
                "Автообновление отключено для Traymetry, запущенной от администратора. " +
                "Перезапустите приложение обычным способом и повторите проверку обновлений.",
                "Automatic updates are disabled for Traymetry started as administrator. " +
                "Restart the application the usual way and check for updates again."
            };
            table["update.hashCheckFailed"] = new[]
            {
                "Проверка SHA-256 обновления не пройдена.",
                "The SHA-256 check of the update failed."
            };
            table["update.helperFailed"] = new[]
            {
                "Не удалось запустить помощник обновления.",
                "Could not start the update helper."
            };
            table["update.folderUnknown"] = new[]
            {
                "Папка Traymetry не определена.",
                "The Traymetry folder could not be determined."
            };
            table["update.protectedFolder"] = new[]
            {
                "Traymetry находится в защищённой папке. Для этой установки " +
                "используйте новый установщик со страницы релиза.",
                "Traymetry sits in a protected folder. Use the new installer from " +
                "the release page for this installation."
            };
            table["update.installedFolderUnknown"] = new[]
            {
                "Папка установленного EXE не определена.",
                "The folder of the installed executable could not be determined."
            };
            table["update.stagedCheckFailed"] = new[]
            {
                "Проверка подготовленного EXE не пройдена.",
                "The check of the staged executable failed."
            };
            table["update.installedCheckFailed"] = new[]
            {
                "Проверка установленного EXE не пройдена; прежняя версия восстановлена.",
                "The check of the installed executable failed; the previous version was restored."
            };
            table["update.exitTimeout"] = new[]
            {
                "Traymetry не завершилась за 30 секунд.",
                "Traymetry did not exit within 30 seconds."
            };
            table["update.badVersion"] = new[] { "Некорректная версия: {0}", "Invalid version: {0}" };

            // -- sensor fallbacks ------------------------------------------
            table["sensor.fanFallback"] = new[] { "Вентилятор", "Fan" };

            // -- help sheet ------------------------------------------------
            table["menu.sensors"] = new[] { "Датчики", "Sensors" };
            table["menu.help"] = new[] { "Подсказки и управление…", "Controls and tips…" };
            table["access.language"] = new[] { "Язык интерфейса", "Interface language" };
            table["tip.language"] = new[]
            {
                "Язык интерфейса: Русский. Клик — переключить на English",
                "Interface language: English. Click to switch to Русский"
            };
            table["help.title"] = new[]
            {
                "Traymetry — подсказки и управление",
                "Traymetry — controls and tips"
            };
            table["help.section.mouse"] = new[] { "Мышь", "Mouse" };
            table["help.section.keys"] = new[] { "Клавиши", "Keyboard" };
            table["help.section.buttons"] = new[] { "Кнопки верхней панели", "Top bar buttons" };
            table["help.section.tips"] = new[] { "Советы", "Tips" };

            table["help.key.leftDrag"] = new[] { "Левая кнопка", "Left button" };
            table["help.text.leftDrag"] = new[] { "Перетащить окно", "Move the window" };
            table["help.key.doubleClick"] = new[] { "Двойной клик", "Double click" };
            table["help.text.doubleClick"] = new[]
            {
                "Развернуть окно на максимум. Ещё один двойной клик — свернуть обратно",
                "Open the window up to its largest view. Another double click puts it back"
            };
            table["help.key.rightClick"] = new[] { "Правая кнопка", "Right button" };
            table["help.text.rightClick"] = new[] { "Меню настроек", "Settings menu" };
            table["help.key.showHide"] = new[]
            {
                "Клик по значку в трее",
                "Click the tray icon"
            };
            table["help.text.showHide"] = new[] { "Спрятать или вернуть окно", "Hide or bring back the window" };
            table["help.key.middleClick"] = new[] { "Нажать колесо", "Press the wheel" };
            table["help.text.middleClick"] = new[]
            {
                "Открыть ползунок прозрачности",
                "Open the opacity slider"
            };
            table["help.key.wheel"] = new[] { "Крутить колесо", "Turn the wheel" };
            table["help.text.wheel"] = new[]
            {
                "Менять прозрачность окна",
                "Change how transparent the window is"
            };
            table["help.key.storageClick"] = new[] { "Клик по «Хранилище»", "Click “Storage”" };
            table["help.text.storageClick"] = new[] { "Выбрать диск", "Pick the drive" };
            table["help.key.edges"] = new[] { "Край или угол", "Edge or corner" };
            table["help.text.edges"] = new[] { "Изменить размер", "Resize" };

            table["help.text.f1"] = new[] { "Эта справка", "This cheat sheet" };
            table["help.text.hide"] = new[] { "Убрать окно в трей", "Send the window to the tray" };
            table["menu.report"] = new[]
            {
                "Собрать отчёт о проблеме…",
                "Collect a problem report…"
            };
            table["report.done"] = new[]
            {
                "Отчёт сохранён:\n{0}\n\nВ нём — версия, настройки, состояние службы датчиков и последние записи журнала. Приложите этот файл к описанию проблемы.",
                "The report is saved:\n{0}\n\nIt holds the version, the settings, the state of the sensor service and the latest log entries. Attach this file to your description of the problem."
            };
            table["report.failed"] = new[]
            {
                "Не удалось собрать отчёт: {0}",
                "The report could not be collected: {0}"
            };

            table["menu.preset.save"] = new[] { "Сохранить", "Save" };
            table["menu.graphs.save"] = new[] { "Сохранить", "Save" };
            table["menu.color.savePalette"] = new[] { "Сохранить", "Save" };

            // -- hotkeys ---------------------------------------------------
            table["menu.hotkeys"] = new[] { "Горячие клавиши", "Hotkeys" };
            table["menu.hotkey.pin"] = new[] { "Закрепить окно", "Lock the window" };
            table["menu.hotkey.hide"] = new[] { "Спрятать окно", "Hide the window" };
            table["menu.hotkey.help"] = new[] { "Открыть подсказку", "Open the cheat sheet" };
            table["menu.hotkey.dismiss"] = new[] { "Убрать окно в трей", "Send the window to the tray" };
            table["hotkey.scope.global"] = new[]
            {
                "Работает в любой программе, включая игры",
                "Works in any application, games included"
            };
            table["hotkey.scope.window"] = new[]
            {
                "Работает, когда окно Traymetry активно",
                "Works while the Traymetry window is active"
            };
            table["hotkey.none"] = new[] { "не назначена", "not set" };
            table["hotkey.title"] = new[] { "Traymetry — горячая клавиша", "Traymetry — hotkey" };
            table["hotkey.prompt"] = new[]
            {
                "Нажмите сочетание клавиш",
                "Press a key combination"
            };
            table["hotkey.hint"] = new[]
            {
                "Backspace — убрать сочетание, Esc — отмена, Enter — сохранить.",
                "Backspace removes the combination, Esc cancels, Enter saves."
            };
            table["hotkey.needModifier"] = new[]
            {
                "Добавьте Ctrl, Alt или Shift. Одиночная клавиша перестанет печататься во всех программах.",
                "Add Ctrl, Alt or Shift. On its own this key would stop typing everywhere."
            };
            table["hotkey.taken"] = new[]
            {
                "Сочетание занято другой программой",
                "Another application already owns this combination"
            };
            table["hotkey.clear"] = new[] { "Убрать", "Remove" };
            table["hotkey.reset"] = new[]
            {
                "Вернуть сочетания по умолчанию",
                "Restore the default combinations"
            };
            table["hotkey.apply"] = new[] { "Сохранить", "Save" };
            table["hotkey.cancel"] = new[] { "Отмена", "Cancel" };
            table["key.tilde"] = new[] { "Ё", "`" };
            table["key.space"] = new[] { "Пробел", "Space" };

            // The cheat sheet prints the combinations from HotkeyDisplay, which
            // is what they are set to.  Naming them here again would print the
            // shipped default to someone who had already moved them.
            table["help.text.hideHotkey"] = new[]
            {
                "Спрятать окно в трей или вернуть его. Тоже работает из любой программы",
                "Send the window to the tray or bring it back. Also works from any application"
            };
            table["help.text.pinHotkey"] = new[]
            {
                "Закрепить или отпустить окно. Работает из любой программы, в том числе из игры: закреплённое окно пропускает клики насквозь, и мышью его уже не расклеить",
                "Lock or release the window. Works from any application, a game included: a locked window passes clicks through, so the mouse can no longer reach it"
            };
            table["help.text.keysNote"] = new[]
            {
                "F1 и Esc работают, когда окно активно. Два сочетания ниже — всегда; изменить их можно в меню «Горячие клавиши».",
                "F1 and Esc work while the window is active. The two combinations below always do; the Hotkeys menu changes them."
            };

            table["help.text.opacityButton"] = new[]
            {
                "Прозрачность: клик открывает ползунок",
                "Opacity: click opens the slider"
            };
            table["help.text.backgroundButton"] = new[]
            {
                "Убрать подложку — останутся одни цифры",
                "Drop the panel — only the numbers stay"
            };
            table["help.text.languageButton"] = new[] { "Язык интерфейса", "Interface language" };
            table["help.text.cycleButton"] = new[]
            {
                "Листать карточки. Кнопка работает всегда: если видны не все карточки, она показывает следующие, а если помещаются все — сдвигает их порядок, и первая уходит в конец",
                "Cycle the cards. The button always does something: when not every card is on screen it brings up the next ones, and when they all fit it shifts their order, moving the first one to the end"
            };
            table["help.text.arrowButton"] = new[]
            {
                "Убрать окно в трей, как Esc. Вернуть — кликом по значку",
                "Send the window to the tray, same as Esc. Click the icon to bring it back"
            };
            table["help.key.pinButton"] = new[] { "Пин", "Pin" };
            table["help.text.pinButton"] = new[]
            {
                "Закрепить положение. Окно перестаёт двигаться, а клики мыши проходят сквозь него — в игру или в то, что под ним. Нажмите пин ещё раз, чтобы разблокировать",
                "Lock the position. The window stops moving and mouse clicks pass through it — into the game, or into whatever is underneath. Press the pin again to unlock"
            };

            table["help.tip.resize"] = new[]
            {
                "Окно подстраивается под свой размер само. Тяните за угол или за край: карточки и цифры подстроятся автоматически.",
                "The window adapts to its own size. Drag a corner or an edge and the cards and numbers adjust automatically."
            };
            table["help.tip.header"] = new[]
            {
                "Верхняя панель и нижняя полоска появляются под курсором и прячутся, когда он ушёл. В меню → Верхняя панель её можно оставить на виду или убрать совсем.",
                "The top bar and the bottom strip appear under the pointer and leave with it. Menu → Top bar keeps it on screen for good, or drops it for good."
            };
            table["help.tip.customize"] = new[]
            {
                "Карточки, графики и цвета настраиваются через меню по правому клику мыши.",
                "Cards, graphs and colours are set up through the right-click menu."
            };
            table["help.tip.gaming"] = new[]
            {
                "Для игры закрепите окно: кнопка пин на верхней панели или тот же пункт в меню. Окно перестанет двигаться, а клики мыши пойдут в игру, а не в Traymetry.",
                "For a game, lock the window: the pin button on the top bar, or the same item in the menu. The window stops moving and mouse clicks go to the game instead of to Traymetry."
            };
            table["help.tip.streaming"] = new[]
            {
                "Для стрима и записи включите в меню «Не попадать в запись экрана». Окно останется на вашем мониторе, но пропадёт из OBS, Discord и скриншотов.",
                "For streaming and recording, turn on “Hide from screen capture” in the menu. The window stays on your own monitor but disappears from OBS, Discord and screenshots."
            };
            table["help.tip.sensors"] = new[]
            {
                "Если температур CPU нет, откройте меню → «Проверить и починить датчики…». Traymetry поставит системный сервис датчиков и один раз спросит права администратора.",
                "If the CPU temperatures are missing, open the menu → “Check and repair sensors…”. Traymetry installs the sensor system service and asks for administrator rights once."
            };
            return table;
        }
    }
}
