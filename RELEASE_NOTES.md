Night Light Control 0.4.0 incorpora instalación normal para el usuario de Windows.

- Instalador propio **NightLightControl-0.4.0-Setup.exe**, sin administrador.
- Carpeta fija de programa, acceso en Inicio y escritorio opcional.
- Entrada en Aplicaciones instaladas, versión y desinstalador.
- Migración del inicio con Windows y conservación de perfiles existentes.
- Actualizaciones mediante el instalador para mantener actualizado el registro
  de Windows; el modo portable continúa usando la actualización del ejecutable.
- Cierre cooperativo para mantenimiento, con restauración del color.
- Corrección del control al pasar de proyección ampliada a solo segunda pantalla:
  conserva la calidez y reintenta/reaplica al restablecerse las salidas de Windows.

La instalación predeterminada utiliza `%LOCALAPPDATA%\Programs\Night Light Control`.
La desinstalación conserva los perfiles en `%LOCALAPPDATA%\NightLightControl`.
Los componentes auxiliares y registros de diagnóstico de esa carpeta también
se conservan. No se elimina el proyecto ni las copias portables previas.

Para instalar, descarga el archivo **Setup.exe** de esta versión y pulsa Instalar.
El ZIP sigue disponible para uso portable. Las pruebas no modifican los colores
de las pantallas del usuario.
