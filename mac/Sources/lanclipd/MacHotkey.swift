import Carbon.HIToolbox
import Foundation
import CoreGraphics

/// Глобальный хоткей Ctrl+Shift+V через Carbon `RegisterEventHotKey`.
///
/// Carbon, а не `NSEvent.addGlobalMonitorForEvents` — по двум причинам: во-первых,
/// `lanclipd` голый SPM-бинарник без `NSApplication`/`RunLoop` в режиме приложения,
/// а `RegisterEventHotKey` работает поверх обычного `RunLoop.current.run()`, который
/// уже крутится в `serve`; во-вторых (и это главное), регистрация глобального хоткея
/// через Carbon не требует разрешения Accessibility вовсе — в отличие от синтеза
/// нажатия через `CGEvent` ниже, которому оно нужно отдельно и выдаётся вручную.
public final class MacHotkey {
    /// Идентификатор нашего хоткея внутри процесса — Carbon позволяет регистрировать
    /// несколько хоткеев на один обработчик событий, различая их по `EventHotKeyID`.
    /// У нас он всего один, но GetEventParameter всё равно обязан быть проверен —
    /// иначе обработчик среагирует на чужой хоткей, случайно зарегистрированный
    /// другим кодом в этом же процессе.
    private static let signature: OSType = 0x6C_63_6C_70 // 'lclp'
    private static let hotKeyIDValue: UInt32 = 1

    private let onPress: () -> Void
    private var hotKeyRef: EventHotKeyRef?
    private var eventHandlerRef: EventHandlerRef?

    public init(onPress: @escaping () -> Void) {
        self.onPress = onPress
    }

    /// Регистрирует Ctrl+Shift+V как глобальный хоткей. Идемпотентности не гарантирует —
    /// вызывающая сторона обязана вызвать `register()` ровно один раз за время жизни
    /// экземпляра (наш единственный вызывающий, `serve`, так и делает).
    public func register() throws {
        var eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard),
                                       eventKind: UInt32(kEventHotKeyPressed))
        let selfPointer = Unmanaged.passUnretained(self).toOpaque()

        var handlerRef: EventHandlerRef?
        let installStatus = InstallEventHandler(
            GetApplicationEventTarget(),
            { _, eventRef, userData -> OSStatus in
                guard let eventRef, let userData else {
                    return OSStatus(eventNotHandledErr)
                }

                var pressedID = EventHotKeyID()
                let paramStatus = GetEventParameter(
                    eventRef,
                    EventParamName(kEventParamDirectObject),
                    EventParamType(typeEventHotKeyID),
                    nil,
                    MemoryLayout<EventHotKeyID>.size,
                    nil,
                    &pressedID)

                guard paramStatus == noErr,
                      pressedID.signature == MacHotkey.signature,
                      pressedID.id == MacHotkey.hotKeyIDValue else {
                    return OSStatus(eventNotHandledErr)
                }

                let hotkey = Unmanaged<MacHotkey>.fromOpaque(userData).takeUnretainedValue()
                hotkey.onPress()
                return noErr
            },
            1,
            &eventType,
            selfPointer,
            &handlerRef)

        guard installStatus == noErr else {
            throw HotkeyError.installHandlerFailed(installStatus)
        }
        eventHandlerRef = handlerRef

        let hotKeyID = EventHotKeyID(signature: MacHotkey.signature, id: MacHotkey.hotKeyIDValue)
        var registeredRef: EventHotKeyRef?
        let registerStatus = RegisterEventHotKey(
            UInt32(kVK_ANSI_V),
            UInt32(controlKey | shiftKey),
            hotKeyID,
            GetApplicationEventTarget(),
            0,
            &registeredRef)

        guard registerStatus == noErr else {
            // Обработчик события уже установлен — снимаем его, чтобы не оставлять
            // висящий InstallEventHandler без соответствующей регистрации хоткея.
            if let eventHandlerRef {
                RemoveEventHandler(eventHandlerRef)
                self.eventHandlerRef = nil
            }
            throw HotkeyError.registerFailed(registerStatus)
        }
        hotKeyRef = registeredRef
    }

    /// Снимает хоткей и обработчик события. Безопасно вызывать повторно и без
    /// предшествующего успешного `register()`.
    public func unregister() {
        if let hotKeyRef {
            UnregisterEventHotKey(hotKeyRef)
            self.hotKeyRef = nil
        }
        if let eventHandlerRef {
            RemoveEventHandler(eventHandlerRef)
            self.eventHandlerRef = nil
        }
    }

    deinit {
        unregister()
    }
}

public enum HotkeyError: Error, Equatable {
    case installHandlerFailed(OSStatus)
    case registerFailed(OSStatus)
}

/// Синтезирует вставку (Cmd+V) в активное приложение.
///
/// Ключевой момент: в момент срабатывания хоткея Ctrl и Shift физически зажаты
/// пользователем (это часть комбинации Ctrl+Shift+V). Если просто послать Cmd+V
/// поверх них, приложение-получатель увидит Cmd+Ctrl+Shift+V и, скорее всего,
/// проигнорирует событие — это выглядело бы как «хоткей не работает», хотя сам
/// хоткей сработал и буфер уже наполнен. Поэтому сперва явно посылается keyUp для
/// обеих клавиш Ctrl (0x3B, 0x3E) и обеих клавиш Shift (0x38, 0x3C), даётся короткая
/// пауза, чтобы приёмник успел обработать снятие модификаторов, и только затем
/// идёт keyDown/keyUp для V с флагом `.maskCommand`.
public func synthesizePaste() {
    guard let source = CGEventSource(stateID: .combinedSessionState) else {
        return
    }

    let leftControl: CGKeyCode = 0x3B
    let rightControl: CGKeyCode = 0x3E
    let leftShift: CGKeyCode = 0x38
    let rightShift: CGKeyCode = 0x3C
    let vKey: CGKeyCode = 0x09

    for modifierKeyCode in [leftControl, rightControl, leftShift, rightShift] {
        CGEvent(keyboardEventSource: source, virtualKey: modifierKeyCode, keyDown: false)?
            .post(tap: .cghidEventTap)
    }

    // Небольшая пауза, чтобы система успела применить снятие модификаторов до
    // прихода Cmd+V — без неё возможна гонка, где V всё ещё видит зажатые Ctrl/Shift.
    usleep(20_000)

    let keyDown = CGEvent(keyboardEventSource: source, virtualKey: vKey, keyDown: true)
    keyDown?.flags = .maskCommand
    keyDown?.post(tap: .cghidEventTap)

    let keyUp = CGEvent(keyboardEventSource: source, virtualKey: vKey, keyDown: false)
    keyUp?.flags = .maskCommand
    keyUp?.post(tap: .cghidEventTap)
}
