# Auditoría de Night Light Control 0.5.0

Revisión del 24 de noviembre de 2026. Se inspeccionaron el controlador, la curva,
la recuperación, las descargas y la instalación. La instalación 0.4.0 del usuario
permaneció abierta durante la revisión: no se controló el escritorio ni se
cambió el color, la proyección o los perfiles para ejecutar estas pruebas.

## Consumo observado

Muestra pasiva de unos 12 segundos de la versión instalada 0.4.0. El porcentaje
de CPU está normalizado sobre todos los procesadores lógicos. La memoria es el
conjunto de trabajo observado al finalizar la muestra, no un máximo ni una
medición de fuga a largo plazo.

| Proceso | CPU media | Memoria de trabajo | Memoria privada |
| --- | ---: | ---: | ---: |
| Aplicación | 0,026 % | 180,4 MB | 130,8 MB |
| Auxiliar de recuperación | 0,000 % | 36,1 MB | 9,6 MB |

La muestra indica poco uso de CPU en ese intervalo. No representa todas las
cargas ni demuestra que la aplicación tenga el menor consumo posible. No se
ejecutó una segunda instancia activa de 0.5.0 sobre las pantallas para comparar
el consumo total.

Se midieron también 100.000 evaluaciones de la misma curva, en un proceso
separado y después de un calentamiento. La versión anterior asignó 100.800.216
bytes temporales y tardó 82,2 ms; el nuevo cálculo asignó 216 bytes para toda
la medición y tardó 62,3 ms. La suma de los resultados fue idéntica (3.236.165).
El tiempo depende del equipo y de su carga. Esta reducción afecta al cálculo
de la curva: no significa que toda la aplicación utilice esa cantidad de RAM.

## Fallos y correcciones

| Hallazgo | Corrección |
| --- | --- |
| Cada evaluación validaba con colecciones y ordenaba todos los puntos. | Validación con un mapa de minutos en la pila y búsqueda lineal de los puntos vecinos, sin colecciones temporales. |
| Copiar y esperar al auxiliar de recuperación bloqueaba el hilo de la interfaz. | Preparación en segundo plano. El controlador espera su confirmación antes de aplicar color. |
| Una copia de recuperación dañada podía reutilizarse. | Comparación de tamaño y SHA256; sustitución mediante un archivo temporal. |
| Una desconexión prolongada mantenía comprobaciones rápidas innecesarias. | Reintentos rápidos iniciales y luego un segundo entre comprobaciones. |
| Errores ajenos al motor podían presentarse como una reconexión de pantalla. | Solo los fallos de lectura o aplicación nativa entran en esa recuperación. |
| La descarga notificaba progreso por cada bloque recibido. | Notificaciones espaciadas al menos 200 ms, más la finalización. |
| Salir durante una descarga podía dejar trabajo o archivos pendientes. | Cancelación y limpieza antes de salir; se vuelve a comprobar la cancelación después de verificar el archivo. |
| La espera del cuerpo de una respuesta podía prolongarse sin límite propio. | Cancelación vinculada con un límite de seis minutos para toda la operación. |
| Un paquete incompleto podía fallar después de comenzar a copiar archivos. | Validación y extracción completa en una carpeta privada antes de modificar el destino. |
| Rutas equivalentes de Windows podían repetirse en un paquete. | Rechazo de duplicados, segmentos inválidos y enlaces inesperados en el destino. |
| La desinstalación seleccionaba documentos por extensión. | Lista explícita de archivos instalados; alternativa limitada para instalaciones 0.4.0 sin lista. Los documentos ajenos se conservan. |
| La reinstalación no conservaba el ejecutable anterior anunciado por la interfaz. | Sustitución del ejecutable con respaldo `.previous`, también al actualizar una instalación registrada. |

La preparación del paquete no garantiza una instalación atómica ante pérdida de
energía, falta de espacio o un bloqueo posterior de archivos. En esos casos se
informa del fallo y puede ser necesario ejecutar otra vez el instalador.

## Validación y límites

Las pruebas locales ejercitan el controlador real con pantallas y reloj
simulados, curvas desordenadas y editadas, continuidad a medianoche, fallos de
protección, reconexión, descargas sin red, cancelaciones, hashes incorrectos y
archivos de instalación en carpetas temporales. También ejecutan el auxiliar
desarmado y observan cierres de procesos de prueba. No restauran ni modifican
las pantallas reales.

GitHub Actions compila las distribuciones y comprueba instalación, reinstalación,
registro, acceso de Inicio y desinstalación en un Windows limpio. La prueba exige
que se conserven tanto los perfiles como un documento añadido a la instalación.
Las vistas de interfaz se generan de forma pasiva desde el XAML real.

No se ha forzado un cierre de la aplicación que controla las pantallas del
usuario, ni realizado una prueba de resistencia de varias horas. Quedan fuera
la apariencia física, HDR, videojuegos, suspensión real y todas las combinaciones
de proyección o controladores gráficos. El auxiliar tampoco puede garantizar la
restauración si se terminan ambos procesos o Windows rechaza el cambio.
