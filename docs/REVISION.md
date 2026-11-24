# Revisión de Night Light Control 0.3.0

Se revisaron el controlador, persistencia, edición de curvas, ciclo de cierre,
actualizador y acceso al motor de pantallas. La versión anterior investigada
fue 0.2.1. No se utilizó control remoto ni se alteró la calidez real del usuario.

| Hallazgo | Corrección y evidencia |
| --- | --- |
| Al desactivar se restauraba Windows en un solo paso. | Salida gradual desde la calidez aplicada, con pruebas de monotonicidad, velocidad, finalización y reactivación a mitad de transición. |
| La interfaz ocultaba el indicador al pasar Enabled a false. | Se muestra mientras el controlador conserva el color durante el fade-out. |
| Un error de guardado borraba el indicador de cambios pendientes. | Coordinador que conserva los cambios, informa del error, reintenta y bloquea el cierre/actualización si no se pueden guardar. Pruebas de fallo y recuperación. |
| Respaldar una configuración corrupta/bloqueada podía lanzar otra excepción durante el arranque. | Recuperación tolerante que impide sustituir el original antes de respaldarlo. Prueba con archivo bloqueado y conservación de bytes. |
| Restaurar después de usar el programa podía sobrescribir la vista previa del deslizador de Windows. | Seguimiento de quién controla el color y comprobación de la vista previa también al restaurar/salir. |
| Las intensidades exactas se mostraban como enteros al seleccionar un punto. | Texto sin redondeo destructivo y guardado de valores decimales. |
| La preferencia admitía hasta 120 segundos pero el control gráfico llegaba a 60. | Rango del control coherente con la configuración. |
| Un registro de versión incompleto o con digest inválido bloqueaba las demás actualizaciones. | Se descarta esa entrada y se mantienen las versiones válidas. Pruebas de ambos casos. |
| Una pantalla sin la interfaz privada requerida abortaba la enumeración completa. | Se omite únicamente esa pantalla cuando devuelve E_NOINTERFACE. Revisado frente al comportamiento de referencia; no se ha probado conectando una pantalla virtual física. |
| Un fallo no controlado de interfaz se daba por manejado sin soltar el motor. | Se desactiva el control y se solicita restauración antes de informar del error. |
| Una terminación forzada no ejecutaba Dispose/Restore. | Auxiliar independiente, confirmación de inicio antes de aplicar color, estado atómico y comprobación de PID/hora de arranque. |

## Condición de Windows tras un cierre

Los registros guardados de Luz nocturna permanecen intactos. La temperatura en
ejecución es responsabilidad del servicio/pipeline de Windows y puede sobrevivir
al proceso que la pidió. Por ello un cierre forzado sin recuperación puede dejar
la última calidez visible hasta que Windows reaplique su estado.

La nueva protección espera a que termine el proceso principal y consulta de
nuevo lo que Windows solicita. En un cierre normal la restauración del padre
desarma el auxiliar; al fallar el padre, el auxiliar intenta restaurar y reintenta
hasta tres veces. La vista previa del deslizador nativo tiene prioridad.

## Validación y límites

Las pruebas ejecutan el controlador real contra una pantalla simulada, incluyen
fallos de escritura, vista previa de Windows, pausa, bloqueo, suspensión,
conflicto con f.lux, recuperación de errores y edición persistida. Se terminan
procesos de prueba para comprobar la detección de cierre forzado y se ejecuta el
auxiliar real desarmado, sin acceder al motor nativo. También se generan vistas
de las cuatro secciones y un tamaño compacto desde el XAML real.

No se ha matado la aplicación del usuario mientras controla las pantallas. No
se ha comprobado colorimétricamente la restauración física, HDR o videojuegos.
Terminar ambos procesos o todo el árbol impide la recuperación independiente;
un fallo de Windows/controlador también puede impedirla. El recurso manual es
activar/desactivar Luz nocturna en la configuración de Windows.
