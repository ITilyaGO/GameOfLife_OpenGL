#version 330 core
out vec4 FragColor;

uniform float iTime;

void main()
{
    float r = 0.5 + 0.5 * sin(iTime);
    float g = 0.5 + 0.5 * sin(iTime + 2.0);
    float b = 0.5 + 0.5 * sin(iTime + 4.0);

    FragColor = vec4(r, g, b, 1.0);
}